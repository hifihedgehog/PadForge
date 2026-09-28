using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Engine;
using PadForge.Engine.Common.AnalogKeyboard;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Analog keyboard protocols (issue #468). No analog keyboard is on the
    /// bench, so every family is pinned against byte fixtures built from the
    /// reference formats: Soup's AnalogueKeyboard.cpp and the AnalogSense
    /// JavaScript SDK (both MIT), the Wooting Analog SDK's device.rs
    /// (MPL-2.0), and the Tartarus Pro commit on DenkiSuki's Soup fork. The
    /// fixtures are what Windows hands ReadFile: byte 0 is the report ID, or 0
    /// for a collection without one.
    /// </summary>
    public class AnalogKeyboardProtocolTests
    {
        private static AnalogKeyInputState Keys() => new AnalogKeyInputState();

        private static byte[] Report(int length, params byte[] head)
        {
            var r = new byte[length];
            Array.Copy(head, r, head.Length);
            return r;
        }

        // ── Wooting ──

        [Fact]
        public void WootingV1_ReadsBigEndianCodes_AndSkipsZeroEntries()
        {
            // 0 (no report ID), W at 200, an all-zero entry, S at 50. The
            // Wooting SDK filters zero entries instead of stopping at them.
            var raw = Report(49, 0x00, 0x00, 0x1A, 200, 0x00, 0x00, 0x00, 0x00, 0x16, 50);
            var k = Keys();
            Assert.True(AnalogKeyboardParsers.ParseWootingV1(raw, k, legacyFirmware: false));
            Assert.Equal(2, k.Count);
            Assert.Equal(200 / 255f, k.Get(AnalogKeyCodes.W), 5);
            Assert.Equal(50 / 255f, k.Get(AnalogKeyCodes.S), 5);
        }

        [Fact]
        public void WootingV1_RepeatedKey_KeepsTheLastValue_AndReplacesTheSet()
        {
            var k = Keys();
            k.Set(AnalogKeyCodes.A, 1f);
            var raw = Report(49, 0x00, 0x00, 0x1A, 100, 0x00, 0x1A, 150);
            AnalogKeyboardParsers.ParseWootingV1(raw, k, legacyFirmware: false);
            Assert.Equal(1, k.Count);
            Assert.Equal(150 / 255f, k.Get(AnalogKeyCodes.W), 5);
            Assert.Equal(0f, k.Get(AnalogKeyCodes.A));
        }

        [Fact]
        public void WootingV1_OldFirmwareOneAndTwo_ScaleBy1Point2_Clamped()
        {
            var raw = Report(49, 0x00, 0x00, 0x1A, 100, 0x00, 0x04, 255);
            var k = Keys();
            AnalogKeyboardParsers.ParseWootingV1(raw, k, legacyFirmware: true);
            Assert.Equal(120 / 255f, k.Get(AnalogKeyCodes.W), 4);
            Assert.Equal(1f, k.Get(AnalogKeyCodes.A));
        }

        [Fact]
        public void WootingV2_Decodes10BitValues_AndNamespaces()
        {
            // W at 1023: high byte 0xFF, low bits 3 in bits 6-7, actuated.
            // Play/Pause (consumer namespace 3, key 0xCD) at 512.
            var raw = Report(65,
                0x00,
                0x22, 0x1A, 0xC1, 0xFF,
                0x00, 0xCD, 0x0C, 0x80);
            var k = Keys();
            Assert.True(AnalogKeyboardParsers.ParseWootingV2(raw, k));
            Assert.Equal(1f, k.Get(AnalogKeyCodes.W));
            Assert.Equal(512 / 1023f, k.Get(AnalogKeyCodes.PlayPause), 5);
            Assert.Equal(2, k.Count);
        }

        // ── Razer ──

        [Fact]
        public void RazerV2_Report7_MapsRazerNumbersToHidCodes()
        {
            // Razer 0x12 is W, 0x3D is Space, 0 ends the list.
            var raw = Report(64, 0x07, 0x12, 128, 0x3D, 255, 0x00, 0x00, 0x2E, 40);
            var k = Keys();
            Assert.True(AnalogKeyboardParsers.ParseRazerHuntsmanV2(raw, k));
            Assert.Equal(128 / 255f, k.Get(AnalogKeyCodes.W), 5);
            Assert.Equal(1f, k.Get(AnalogKeyCodes.Space));
            Assert.Equal(0f, k.Get(AnalogKeyCodes.Z));
        }

        [Fact]
        public void RazerV2_AnotherReportId_LeavesTheStateAlone()
        {
            var k = Keys();
            k.Set(AnalogKeyCodes.W, 0.5f);
            var raw = Report(64, 0x05, 0x12, 255);
            Assert.False(AnalogKeyboardParsers.ParseRazerHuntsmanV2(raw, k));
            Assert.Equal(0.5f, k.Get(AnalogKeyCodes.W));
        }

        [Fact]
        public void RazerV3_Report11_ReadsTriples()
        {
            // A (0x1F) at 64 with its unused third byte, S (0x20) at 255.
            var raw = Report(64, 0x0B, 0x1F, 64, 0x99, 0x20, 255, 0x00);
            var k = Keys();
            Assert.True(AnalogKeyboardParsers.ParseRazerHuntsmanV3(raw, k));
            Assert.Equal(64 / 255f, k.Get(AnalogKeyCodes.A), 5);
            Assert.Equal(1f, k.Get(AnalogKeyCodes.S));
            Assert.Equal(2, k.Count);
        }

        [Fact]
        public void TartarusPro_Report6_ReadsTwentyPositions()
        {
            var raw = new byte[24];
            raw[0] = 0x06;
            raw[1 + 7] = 255;   // position 8 is W
            raw[1 + 11] = 128;  // position 12 is A
            raw[1 + 20] = 99;   // past the 20 analog keys: not a key
            var k = Keys();
            Assert.True(AnalogKeyboardParsers.ParseRazerTartarusPro(raw, k));
            Assert.Equal(2, k.Count);
            Assert.Equal(1f, k.Get(AnalogKeyCodes.W));
            Assert.Equal(128 / 255f, k.Get(AnalogKeyCodes.A), 5);
        }

        // ── NuPhy ──

        [Fact]
        public void NuPhy_OneKeyPerReport_UpdatesThatKey_AndZeroReleasesIt()
        {
            var k = Keys();
            k.Set(AnalogKeyCodes.A, 0.3f);
            // 0 (no report ID), type 0xA0, one unknown byte, W, value 400.
            var press = Report(9, 0x00, 0xA0, 0x10, 0x00, 0x1A, 0x01, 0x90);
            Assert.True(AnalogKeyboardParsers.ParseNuPhy(press, k, productId: 0x6130));
            Assert.Equal(0.5f, k.Get(AnalogKeyCodes.W), 5);
            Assert.Equal(0.3f, k.Get(AnalogKeyCodes.A)); // other keys persist

            var release = Report(9, 0x00, 0xA0, 0x10, 0x00, 0x1A, 0x00, 0x00);
            AnalogKeyboardParsers.ParseNuPhy(release, k, productId: 0x6130);
            Assert.Equal(0f, k.Get(AnalogKeyCodes.W));
        }

        [Fact]
        public void NuPhy_Air75AndAir60_UseTheLongerScale_AndModifierBits()
        {
            var k = Keys();
            var shift = Report(9, 0x00, 0xA0, 0x10, 0x02, 0x00, 0x01, 0x90);
            AnalogKeyboardParsers.ParseNuPhy(shift, k, productId: 0x6120);
            Assert.Equal(400 / 1600f, k.Get(AnalogKeyCodes.LShift), 5);
            Assert.Equal(AnalogKeyCodes.Fn, AnalogKeyCodes.NuPhyToCode(0xFF05));
        }

        [Fact]
        public void NuPhy_OtherReportTypes_AreNotKeys()
        {
            var k = Keys();
            Assert.False(AnalogKeyboardParsers.ParseNuPhy(Report(9, 0x00, 0x01, 0x02), k, 0x6130));
            Assert.Equal(0, k.Count);
        }

        // ── Code tables ──

        [Theory]
        [InlineData(0x12, AnalogKeyCodes.W)]
        [InlineData(0x3D, AnalogKeyCodes.Space)]
        [InlineData(0x6E, AnalogKeyCodes.Escape)]
        [InlineData(0x6C, AnalogKeyCodes.NumpadEnter)]
        [InlineData(0x7F, AnalogKeyCodes.LMeta)]
        [InlineData(0x81, AnalogKeyCodes.ContextMenu)]
        [InlineData(0x2D, AnalogKeyCodes.IntlBackslash)]
        [InlineData(0x3B, AnalogKeyCodes.Fn)]
        [InlineData(0x0E, AnalogKeyCodes.None)]
        public void RazerTable_MatchesTheReferences(int razer, int code)
        {
            Assert.Equal(code, AnalogKeyCodes.RazerToCode(razer));
        }

        [Fact]
        public void TartarusOrder_MatchesTheFactoryAssignments()
        {
            // HallJoy's published kFactoryHids fact table, the same order the
            // Soup fork's tartarusProCurrentMap lists by key name.
            int[] factory = { 30, 31, 32, 33, 34, 43, 20, 26, 8, 21, 57, 4, 22, 7, 9, 225, 29, 27, 6, 44 };
            Assert.Equal(factory, AnalogKeyCodes.TartarusPro);
        }

        [Fact]
        public void DrunkDeerMenuKey_IsTheContextMenuKey()
        {
            Assert.Equal(AnalogKeyCodes.ContextMenu, AnalogKeyCodes.DrunkDeerToCode(5 * 21 + 12));
            Assert.Equal(AnalogKeyCodes.W, AnalogKeyCodes.DrunkDeerToCode(2 * 21 + 2));
            Assert.Equal(AnalogKeyCodes.None, AnalogKeyCodes.DrunkDeerToCode(0 * 21 + 1));
        }

        [Fact]
        public void BytechTable_MatchesAnalogSense()
        {
            Assert.Equal(AnalogKeyCodes.W, AnalogKeyCodes.BytechToCode(30));
            Assert.Equal(AnalogKeyCodes.Space, AnalogKeyCodes.BytechToCode(70));
            Assert.Equal(AnalogKeyCodes.ArrowDown, AnalogKeyCodes.BytechToCode(75));
            Assert.Equal(AnalogKeyCodes.ArrowLeft, AnalogKeyCodes.BytechToCode(76));
            Assert.Equal(AnalogKeyCodes.PageDown, AnalogKeyCodes.BytechToCode(103));
            Assert.Equal(AnalogKeyCodes.None, AnalogKeyCodes.BytechToCode(101));
        }

        [Fact]
        public void VirtualKeysAndScanCodes_FollowTheUsLayout()
        {
            Assert.Equal(0x57, AnalogKeyCodes.UsVirtualKey(AnalogKeyCodes.W));
            Assert.Equal(0x67, AnalogKeyCodes.UsVirtualKey(AnalogKeyCodes.Numpad7));
            Assert.Equal(0xA0, AnalogKeyCodes.UsVirtualKey(AnalogKeyCodes.LShift));
            Assert.Equal(0xB0, AnalogKeyCodes.UsVirtualKey(AnalogKeyCodes.NextTrack));
            Assert.Equal(0x7C, AnalogKeyCodes.UsVirtualKey(AnalogKeyCodes.F13));
            Assert.Equal(0, AnalogKeyCodes.UsVirtualKey(AnalogKeyCodes.Fn));
            Assert.Equal(0x11, AnalogKeyCodes.Ps2Scancode(AnalogKeyCodes.W));
            Assert.Equal(0xE048, AnalogKeyCodes.Ps2Scancode(AnalogKeyCodes.ArrowUp));
            Assert.Equal(0, AnalogKeyCodes.Ps2Scancode(AnalogKeyCodes.Fn));
        }

        [Fact]
        public void Layouts_HaveTheReferenceShapes()
        {
            Assert.Equal(6 * 15, AnalogKeyCodes.KeychronQ1He.Size);
            Assert.Equal(6 * 16, AnalogKeyCodes.KeychronQ3He.Size);
            Assert.Equal(6 * 19, AnalogKeyCodes.KeychronQ5He.Size);
            Assert.Equal(6 * 16, AnalogKeyCodes.KeychronK2He.Size);
            Assert.Equal(6 * 15, AnalogKeyCodes.LemokeyP1HeAnsi.Size);
            Assert.Equal(6 * 15, AnalogKeyCodes.LemokeyP1HeIso.Size);
            Assert.Equal(5 * 14, AnalogKeyCodes.MadlionsMad60He.Size);
            Assert.Equal(5 * 15, AnalogKeyCodes.MadlionsMad68He.Size);
            // Spot keys at their row and column.
            Assert.Equal(AnalogKeyCodes.W, AnalogKeyCodes.KeychronQ1He.Keys[2 * 15 + 2]);
            Assert.Equal(AnalogKeyCodes.Fn, AnalogKeyCodes.KeychronQ5He.Keys[5 * 19 + 10]);
            Assert.Equal(AnalogKeyCodes.IntlBackslash, AnalogKeyCodes.LemokeyP1HeIso.Keys[4 * 15 + 1]);
            Assert.Equal(AnalogKeyCodes.ContextMenu, AnalogKeyCodes.MadlionsMad60He.Keys[4 * 14 + 11]);
        }

        // ── Catalog ──

        [Fact]
        public void Identify_EachFamily_ByTheReferencesRules()
        {
            bool Id7(byte id) => id == 7;
            bool Id11(byte id) => id == 11;
            bool Id6(byte id) => id == 6;
            bool Id4(byte id) => id == 4;
            bool None(byte id) => false;

            Assert.Equal(AnalogKeyboardProtocol.WootingV1, AnalogKeyboardCatalog.Identify(0x31E3, 0x1230, 0xFF54, 0, None));
            Assert.Equal(AnalogKeyboardProtocol.WootingV2, AnalogKeyboardCatalog.Identify(0x31E3, 0x1232, 0xFF53, 0, None));
            Assert.Equal(AnalogKeyboardProtocol.None, AnalogKeyboardCatalog.Identify(0x31E3, 0x1230, 0x0001, 6, None));
            Assert.Equal(AnalogKeyboardProtocol.WootingV1, AnalogKeyboardCatalog.Identify(0x03EB, 0xFF02, 0xFF54, 0, None));
            Assert.Equal(AnalogKeyboardProtocol.None, AnalogKeyboardCatalog.Identify(0x03EB, 0x2FF4, 0xFF54, 0, None));

            Assert.Equal(AnalogKeyboardProtocol.RazerHuntsmanV2, AnalogKeyboardCatalog.Identify(0x1532, 0x0266, 0xFF00, 1, Id7));
            Assert.Equal(AnalogKeyboardProtocol.None, AnalogKeyboardCatalog.Identify(0x1532, 0x0266, 0xFF00, 1, Id11));
            Assert.Equal(AnalogKeyboardProtocol.RazerHuntsmanV3, AnalogKeyboardCatalog.Identify(0x1532, 0x02B0, 0xFF00, 1, Id11));
            Assert.Equal(AnalogKeyboardProtocol.RazerTartarusPro, AnalogKeyboardCatalog.Identify(0x1532, 0x0244, 0xFF00, 1, Id6));
            Assert.Equal(AnalogKeyboardProtocol.None, AnalogKeyboardCatalog.Identify(0x1532, 0x02CF, 0xFF00, 1, Id11));

            Assert.Equal(AnalogKeyboardProtocol.NuPhy, AnalogKeyboardCatalog.Identify(0x19F5, 0x6130, 1, 0, None));
            Assert.Equal(AnalogKeyboardProtocol.None, AnalogKeyboardCatalog.Identify(0x19F5, 0x6130, 1, 6, None));
            Assert.Equal(AnalogKeyboardProtocol.DrunkDeer, AnalogKeyboardCatalog.Identify(0x352D, 0x2383, 0xFF00, 1, Id4));
            Assert.Equal(AnalogKeyboardProtocol.Keychron, AnalogKeyboardCatalog.Identify(0x3434, 0x0B10, 0xFF60, 0x61, None));
            Assert.Equal(AnalogKeyboardProtocol.None, AnalogKeyboardCatalog.Identify(0x3434, 0x0B99, 0xFF60, 0x61, None));
            Assert.Equal(AnalogKeyboardProtocol.Keychron, AnalogKeyboardCatalog.Identify(0x362D, 0x0611, 0xFF60, 0x61, None));
            Assert.Equal(AnalogKeyboardProtocol.Madlions, AnalogKeyboardCatalog.Identify(0x373B, 0x1054, 0xFF60, 0x61, None));
            Assert.Equal(AnalogKeyboardProtocol.Bytech, AnalogKeyboardCatalog.Identify(0x372E, 0x105B, 0xFF00, 1, None));
        }

        [Fact]
        public void WootingIdentity_MasksTheModeBits_OthersKeepTheirPid()
        {
            Assert.Equal(0x1230, AnalogKeyboardCatalog.IdentityProductId(0x31E3, 0x1232));
            Assert.Equal(0x0266, AnalogKeyboardCatalog.IdentityProductId(0x1532, 0x0266));
        }

        [Fact]
        public void Madlions1054_IsAMad60He()
        {
            Assert.Same(AnalogKeyCodes.MadlionsMad60He, AnalogKeyboardCatalog.MadlionsLayout(0x1054));
            Assert.Same(AnalogKeyCodes.MadlionsMad68He, AnalogKeyboardCatalog.MadlionsLayout(0x10A7));
        }

        [Fact]
        public void KeysFor_ListsWhatTheFamilyCanReport()
        {
            Assert.Equal(AnalogKeyCodes.TartarusPro, AnalogKeyboardCatalog.KeysFor(0x1532, 0x0244));
            var mad60 = AnalogKeyboardCatalog.KeysFor(0x373B, 0x1053);
            Assert.Contains(AnalogKeyCodes.W, mad60);
            Assert.DoesNotContain(AnalogKeyCodes.F1, mad60);
            Assert.Equal(mad60.Length, mad60.Distinct().Count());
            Assert.Equal(AnalogKeyCodes.FullKeyboard, AnalogKeyboardCatalog.KeysFor(0x31E3, 0x1230));
        }

        // ── Polled families ──

        /// <summary>A scripted keyboard: each request is answered by the
        /// responder with zero or more reports, queued for Receive.</summary>
        private sealed class FakeTransport : IAnalogKeyboardTransport
        {
            private readonly Func<byte[], IEnumerable<byte[]>> _respond;
            private readonly Queue<byte[]> _pending = new();
            public readonly List<byte[]> Sent = new();
            public int Discards;

            /// <summary>The device went away: writes fail and reads say so.</summary>
            public bool Gone;

            public FakeTransport(Func<byte[], IEnumerable<byte[]>> respond) => _respond = respond;

            public bool Send(byte[] report)
            {
                if (Gone) return false;
                Sent.Add((byte[])report.Clone());
                foreach (var r in _respond(report)) _pending.Enqueue(r);
                return true;
            }

            public int Receive(byte[] buffer, int timeoutMs)
            {
                if (Gone) return -1;
                if (_pending.Count == 0) return 0;
                var r = _pending.Dequeue();
                Array.Copy(r, buffer, r.Length);
                return r.Length;
            }

            public void DiscardStale() => Discards++;

            public bool SendOutputReport(byte[] report) => Send(report);
            public bool SetFeature(byte[] report) => !Gone;
            public int GetFeature(byte[] buffer) => -1;
            public int InputLength => 65;
            public int OutputLength => 65;
            public int FeatureLength => 0;
        }

        [Fact]
        public void DrunkDeer_ThreeAnswers_ComposeTheGrid()
        {
            byte[] Answer(int index, params (int at, byte value)[] keys)
            {
                var a = new byte[64];
                a[0] = 0x04;
                a[4] = (byte)index;
                foreach (var (at, value) in keys) a[5 + at] = value;
                return a;
            }
            // W at grid 44 (answer 0), Space at 111 (answer 1, offset 52),
            // Right Arrow at 121 (answer 2, offset 3). Arrival order shuffled:
            // the index byte places each answer.
            var io = new FakeTransport(req => new[]
            {
                Answer(1, (52, 40)),
                Answer(0, (44, 20)),
                Answer(2, (3, 10)),
            });
            var output = Keys();
            var poller = new DrunkDeerPoller();
            Assert.Equal(AnalogPollResult.Ok, poller.Pass(io, output, null));
            Assert.Equal(new byte[] { 0x04, 0xB6, 0x03, 0x01 }, io.Sent[0].Take(4).ToArray());
            Assert.Equal(64, io.Sent[0].Length);
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.Space));
            Assert.Equal(0.25f, output.Get(AnalogKeyCodes.ArrowRight));
            Assert.Equal(3, output.Count);
            Assert.Equal(1, io.Discards);
        }

        [Fact]
        public void DrunkDeer_MissingAnswer_IsNoAnswer()
        {
            // Answer 0 arrives, answers 1 and 2 never do.
            var answer = new byte[64];
            answer[0] = 0x04;
            var io = new FakeTransport(req => new[] { answer });
            Assert.Equal(AnalogPollResult.NoAnswer, new DrunkDeerPoller().Pass(io, Keys(), null));
        }

        [Fact]
        public void EveryPolledFamily_ReportsAGoneDevice()
        {
            var pollers = new AnalogKeyboardPoller[]
            {
                new DrunkDeerPoller(), new KeychronPoller(AnalogKeyCodes.KeychronQ1He),
                new MadlionsPoller(AnalogKeyCodes.MadlionsMad60He), new BytechPoller(),
            };
            foreach (var poller in pollers)
            {
                var io = new FakeTransport(req => Array.Empty<byte[]>()) { Gone = true };
                Assert.Equal(AnalogPollResult.Failed, poller.Pass(io, Keys(), null));
            }
        }

        /// <summary>A Keychron answer as ReadFile returns it: 0 (no report
        /// ID), then 32 data bytes starting A9 and the command.</summary>
        private static byte[] KeychronAnswer(byte command, params (int at, byte value)[] data)
        {
            var a = new byte[33];
            a[1] = 0xA9;
            a[2] = command;
            foreach (var (at, value) in data) a[1 + at] = value;
            return a;
        }

        [Fact]
        public void Keychron_AnalogSenseFirmware_ReadsEveryKeyInFourAnswers()
        {
            // Version answer: am_version 4 at data byte 2, 0x45 in the last
            // byte marks the AnalogSense firmware. The full read answers four
            // times with 30 travel bytes after A9 31. W is layout index 32
            // (row 2, column 2 of the Q1 HE), answer 1 byte 2. A is index 46,
            // answer 1 byte 16. Travel under 5 is rest.
            var io = new FakeTransport(req =>
            {
                if (req[2] == 0x01) return new[] { KeychronAnswer(0x01, (2, 4), (31, 0x45)) };
                if (req[2] == 0x31)
                    return new[]
                    {
                        KeychronAnswer(0x31, (2 + 0, 4)),
                        KeychronAnswer(0x31, (2 + 2, 235), (2 + 16, 117)),
                        KeychronAnswer(0x31),
                        KeychronAnswer(0x31),
                    };
                return Array.Empty<byte[]>();
            });
            var poller = new KeychronPoller(AnalogKeyCodes.KeychronQ1He);
            var output = Keys();
            Assert.Equal(AnalogPollResult.Ok, poller.Pass(io, output, null));
            Assert.True(poller.FullReports);
            Assert.Equal(1f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(117 / 235f, output.Get(AnalogKeyCodes.A), 5);
            Assert.Equal(0f, output.Get(AnalogKeyCodes.Escape)); // travel 4 is rest
            Assert.Equal(2, output.Count);
            Assert.Equal(new byte[] { 0x00, 0xA9, 0x31 }, io.Sent[1].Take(3).ToArray());
            Assert.Equal(33, io.Sent[1].Length);
        }

        [Fact]
        public void Keychron_StockFirmware_ReadsHeldKeysAndARotatingGroup()
        {
            // am_version 4 puts the travel at data byte 6, and no 0x45 marks
            // stock firmware. The first pass reads group 0 (layout indices 0
            // to 3) plus any key Windows sees down.
            var asked = new List<(int row, int col)>();
            var io = new FakeTransport(req =>
            {
                if (req[2] == 0x01) return new[] { KeychronAnswer(0x01, (2, 4)) };
                if (req[2] == 0x30)
                {
                    asked.Add((req[3], req[4]));
                    byte travel = req[3] == 2 && req[4] == 2 ? (byte)200 : (byte)0;
                    return new[] { KeychronAnswer(0x30, (6, travel)) };
                }
                return Array.Empty<byte[]>();
            });
            var poller = new KeychronPoller(AnalogKeyCodes.KeychronQ1He);
            var output = Keys();
            Assert.Equal(AnalogPollResult.Ok, poller.Pass(io, output, code => code == AnalogKeyCodes.W));
            Assert.False(poller.FullReports);
            Assert.Equal(new[] { (0, 0), (0, 1), (0, 2), (0, 3), (2, 2) }, asked.ToArray());
            Assert.Equal(200 / 235f, output.Get(AnalogKeyCodes.W), 5);

            // Second pass: W is moving, so it is read again without being
            // held, and the rotation moved on to group 1 (indices 4 to 7).
            asked.Clear();
            poller.Pass(io, output, null);
            Assert.Equal(new[] { (0, 4), (0, 5), (0, 6), (0, 7), (2, 2) }, asked.ToArray());
        }

        [Fact]
        public void Keychron_OldFirmwareVersion_ReadsTheTravelAtByte3()
        {
            var io = new FakeTransport(req =>
            {
                if (req[2] == 0x01) return new[] { KeychronAnswer(0x01, (2, 3)) };
                if (req[2] == 0x30 && req[3] == 2 && req[4] == 2)
                    return new[] { KeychronAnswer(0x30, (3, 235)) };
                return new[] { KeychronAnswer(0x30) };
            });
            var output = Keys();
            new KeychronPoller(AnalogKeyCodes.KeychronQ1He).Pass(io, output, code => code == AnalogKeyCodes.W);
            Assert.Equal(1f, output.Get(AnalogKeyCodes.W));
        }

        [Fact]
        public void Madlions_ReadsFourKeysPerAnswer_PressedKeysFirst()
        {
            // MAD60HE: W is layout index 16 (row 1, column 2), the first key
            // of the chunk at offset 16, which the first pass only reads
            // because Windows sees W down. The answer carries four keys of 5
            // bytes after a 7-byte header, the travel a big-endian u16 in the
            // last two bytes of each.
            var offsets = new List<int>();
            var io = new FakeTransport(req =>
            {
                Assert.Equal(new byte[] { 0x00, 0x02, 0x96, 0x1C }, req.Take(4).ToArray());
                Assert.Equal(4, req[8]);
                int offset = req[7];
                offsets.Add(offset);
                var a = new byte[33];
                if (offset == 16)
                {
                    a[1 + 7 + 3] = 0x00;
                    a[1 + 7 + 4] = 175;
                }
                return new[] { a };
            });
            var output = Keys();
            var poller = new MadlionsPoller(AnalogKeyCodes.MadlionsMad60He);
            Assert.Equal(AnalogPollResult.Ok, poller.Pass(io, output, code => code == AnalogKeyCodes.W));
            Assert.Equal(new[] { 0, 4, 8, 12, 16 }, offsets.ToArray());
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));
        }

        [Fact]
        public void Bytech_RequestChecksum_AndAnswerWithOrWithoutReportId()
        {
            var request = BytechPoller.Request();
            Assert.Equal(64, request.Length);
            Assert.Equal(new byte[] { 0x09, 0x97, 0x00 }, request.Take(3).ToArray());
            // 255 minus (9 + 0x97) mod 256.
            Assert.Equal(0x5F, request[63]);

            // Report ID, 97 01, three bytes, a count of 8 bytes, then W
            // (position 30) at distance 178 and Space (70) at 355.
            var withId = new byte[] { 0x09, 0x97, 0x01, 0, 0, 0, 8, 0x00, 30, 0x00, 178, 0x00, 70, 0x01, 0x63 };
            var output = Keys();
            Assert.True(BytechPoller.ParseAnswer(withId, output));
            Assert.Equal(178 / 355f, output.Get(AnalogKeyCodes.W), 5);
            Assert.Equal(1f, output.Get(AnalogKeyCodes.Space));

            var withoutId = withId.Skip(1).ToArray();
            var again = Keys();
            Assert.True(BytechPoller.ParseAnswer(withoutId, again));
            Assert.True(again.SameAs(output));

            // Distance 10 or less is rest.
            var rest = new byte[] { 0x97, 0x01, 0, 0, 0, 4, 0x00, 30, 0x00, 10 };
            var r = Keys();
            Assert.True(BytechPoller.ParseAnswer(rest, r));
            Assert.Equal(0, r.Count);
            Assert.False(BytechPoller.ParseAnswer(new byte[] { 0x09, 0x96, 0x01 }, Keys()));
        }

        [Fact]
        public void Bytech_Pass_SkipsOtherReports_AndReadsTheAnswer()
        {
            var answer = new byte[] { 0x09, 0x97, 0x01, 0, 0, 0, 4, 0x00, 30, 0x01, 0x63 };
            var io = new FakeTransport(req => new[] { new byte[] { 0x09, 0x20, 0x00 }, answer });
            var output = Keys();
            Assert.Equal(AnalogPollResult.Ok, new BytechPoller().Pass(io, output, null));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(BytechPoller.Request(), io.Sent[0]);
        }
    }
}
