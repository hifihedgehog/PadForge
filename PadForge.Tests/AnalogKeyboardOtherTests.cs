using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using PadForge.Engine;
using PadForge.Engine.Common.AnalogKeyboard;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The routes in <see cref="OtherRoutes"/> (issue #468), pinned against
    /// byte fixtures built from their sources: the Centerpiece Pro commits on
    /// LeiterConsulting's Soup fork (soup/CpproProtocol.hpp,
    /// soup/AnalogueKeyboard.cpp), libhmk (include/commands.h,
    /// src/commands.c) and its configurator hmkconf, HallJoy's M901
    /// diagnostic backend (rog_azoth96he_diagnostic_backend.cpp) and firmware
    /// reconnaissance, and the AnalogSense author's PRO X TKL RAPID capture
    /// gist. Fixtures are what Windows hands ReadFile: byte 0 is the report
    /// ID, or 0 for a collection without one.
    /// </summary>
    public class AnalogKeyboardOtherTests
    {
        private static AnalogKeyInputState Keys() => new();

        private static byte[] Report(int length, params byte[] head)
        {
            var r = new byte[length];
            Array.Copy(head, r, head.Length);
            return r;
        }

        private static byte[] Hex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return bytes;
        }

        private static AnalogKeyboardDeviceInfo Info(ushort vid, ushort pid, ushort usagePage, ushort usage,
            ushort input, ushort output, byte[] inputIds = null, byte[] outputIds = null)
        {
            var inIds = new HashSet<byte>(inputIds ?? Array.Empty<byte>());
            var outIds = new HashSet<byte>(outputIds ?? Array.Empty<byte>());
            return new AnalogKeyboardDeviceInfo
            {
                Path = $@"\\?\hid#vid_{vid:x4}&pid_{pid:x4}#{usagePage:x4}{usage:x4}",
                VendorId = vid,
                ProductId = pid,
                UsagePage = usagePage,
                Usage = usage,
                InputReportLength = input,
                OutputReportLength = output,
                HasInputReport = id => inIds.Contains(id),
                HasOutputReport = id => outIds.Contains(id),
            };
        }

        // ── Route list ─────────────────────────────────────────────────────

        [Fact]
        public void All_ListsTheFourRoutesInOrder_EachMatchingOnlyItsOwnKeyboard()
        {
            // Exact identities: 361D:0200 (AnalogueKeyboard.cpp:206-217),
            // AB50 (keyboard.json:6-7), 0B05:1C10 (backend:28-29), 046D:C35B.
            var all = OtherRoutes.All;
            Assert.Equal(new[] { "finalmouse-centerpiece-pro", "libhmk", "halljoy-rog-azoth-96-he", "logitech-pro-x-tkl-rapid" },
                all.Select(r => r.Id).ToArray());
            Assert.Equal(new[]
            {
                AnalogKeyboardProtocol.FinalmouseCenterpiecePro, AnalogKeyboardProtocol.Libhmk,
                AnalogKeyboardProtocol.RogAzoth96He, AnalogKeyboardProtocol.LogitechRapid,
            }, all.Select(r => r.Protocol).ToArray());
            foreach (var protocol in all.Select(r => r.Protocol))
                Assert.True(Enum.IsDefined(typeof(AnalogKeyboardProtocol), protocol));

            var samples = new[] { CpproInfo(), LibhmkInfo(0xAB60), AzothControl(), RapidInfo() };
            for (int r = 0; r < all.Count; r++)
                for (int s = 0; s < samples.Length; s++)
                    Assert.Equal(r == s, all[r].Matches(samples[s]));

            Assert.IsType<CenterpieceProSession>(all[0].CreateSession(samples[0]));
            Assert.IsType<LibhmkSession>(all[1].CreateSession(samples[1]));
            Assert.IsType<RogAzoth96HeSession>(all[2].CreateSession(samples[2]));
            Assert.IsType<LogitechRapidSession>(all[3].CreateSession(samples[3]));
        }

        [Fact]
        public void Routes_OpenTheWayTheirSourcesDo()
        {
            var all = OtherRoutes.All;
            // Soup's hwHid opens shared, read and write (hwHid.cpp:171-175).
            Assert.True(all[0].Writable);
            Assert.False(all[0].Exclusive);
            // libhmk answers carry only the command ID (commands.c:479-480).
            Assert.True(all[1].Writable);
            Assert.True(all[1].Exclusive);
            // HallJoy opens the control collection shared, read and write,
            // and the event collection beside it (backend:260-265).
            Assert.True(all[2].Writable);
            Assert.False(all[2].Exclusive);
            Assert.NotNull(all[2].Companion);
            // The gist only reads (logitech-analogue-report.cpp:8-11).
            Assert.False(all[3].Writable);
            Assert.False(all[3].Exclusive);
        }

        [Fact]
        public void DataFile_HoldsEveryTableAtItsSize()
        {
            // KEY_LAYOUT's 69 entries (CpproProtocol.hpp:37), keycode_to_hid[256]
            // (keycodes.c:20), and a byte's worth of key IDs for the other two.
            Assert.Equal(69, AnalogKeyboardData.Table(OtherRoutes.DataFile, "cppro").Length);
            Assert.Equal(256, AnalogKeyboardData.Table(OtherRoutes.DataFile, "libhmk_keycodes").Length);
            Assert.Equal(256, AnalogKeyboardData.Table(OtherRoutes.DataFile, "logitech_rapid").Length);
            Assert.Equal(256, AnalogKeyboardData.Table(OtherRoutes.DataFile, "rog_azoth96he").Length);
        }

        // ── Finalmouse Centerpiece Pro ─────────────────────────────────────

        private static AnalogKeyboardDeviceInfo CpproInfo(ushort pid = 0x0200, ushort usagePage = 0xFF00,
            ushort usage = 0x0001, byte[] inputIds = null, byte[] outputIds = null, ushort output = 64)
            => Info(0x361D, pid, usagePage, usage, 64, output, inputIds ?? new byte[] { 4 }, outputIds ?? new byte[] { 3 });

        /// <summary>A live key report, <c>04 08 03</c>, the H index in byte 3
        /// and the travel in byte 6 (CpproProtocol.hpp:69-71).</summary>
        private static byte[] CpproKey(byte index, byte travel, byte type = 3, int length = 64)
        {
            var r = new byte[length];
            r[0] = 0x04;
            r[1] = 0x08;
            r[2] = type;
            r[3] = index;
            r[4] = 0x01;
            r[6] = travel;
            return r;
        }

        [Fact]
        public void Cppro_Matches_TheForksCollection_AndRejectsNearMisses()
        {
            // AnalogueKeyboard.cpp:206-217: 361D:0200, FF00:1, input report 4.
            Assert.True(CenterpieceProProtocol.Matches(CpproInfo()));
            // 0202 is the Centerpiece without Pro (finalmouse_centerpiece
            // gitdocs/ProtocolNotes.md:7), which the fork does not read.
            Assert.False(CenterpieceProProtocol.Matches(CpproInfo(pid: 0x0202)));
            Assert.False(CenterpieceProProtocol.Matches(CpproInfo(usagePage: 0xFF01)));
            Assert.False(CenterpieceProProtocol.Matches(CpproInfo(usage: 0x0002)));
            Assert.False(CenterpieceProProtocol.Matches(CpproInfo(inputIds: new byte[] { 1 })));
            // Output report 3 must belong to the collection the request goes to.
            Assert.False(CenterpieceProProtocol.Matches(CpproInfo(outputIds: new byte[] { 1 })));
            Assert.False(CenterpieceProProtocol.Matches(CpproInfo(output: 3)));
            var otherVendor = CpproInfo();
            otherVendor.VendorId = 0x361E;
            Assert.False(CenterpieceProProtocol.Matches(otherVendor));
            Assert.False(CenterpieceProProtocol.Matches(null));
        }

        [Fact]
        public void Cppro_Request_IsTheForksFrame()
        {
            // KEY_REPORT_REQUEST and OUTPUT_REPORT_SIZE (CpproProtocol.hpp:12-14),
            // padded as getActiveKeysCppro pads it (AnalogueKeyboard.cpp:1277-1279).
            var expected = new byte[64];
            expected[0] = 0x03;
            expected[1] = 0x02;
            expected[2] = 0xF0;
            expected[3] = 0x1D;
            Assert.Equal(expected, CenterpieceProProtocol.Request());
            Assert.Equal(expected, CenterpieceProProtocol.Request(64));
            Assert.Equal(expected, CenterpieceProProtocol.Request(65));
            // A shorter declared report drops only trailing zeros.
            Assert.Equal(expected.Take(33).ToArray(), CenterpieceProProtocol.Request(33));
        }

        [Fact]
        public void Cppro_Start_WritesTheRequestOnce_WithWriteFile()
        {
            // hwHid::sendReport is WriteFile (hwHid.cpp:763-775), sent on the
            // first read (AnalogueKeyboard.cpp:418-421 and 1275-1286).
            var io = new AnalogKeyboardTestTransport();
            long now = 1000;
            var session = new CenterpieceProSession(() => now);
            Assert.True(session.Start(io));
            Assert.Single(io.Log);
            Assert.Equal(CenterpieceProProtocol.Request(), io.Writes("out")[0]);
            Assert.Equal(3500, session.NextRequestAt);
            Assert.Equal("Finalmouse Centerpiece Pro", session.ModelName);
        }

        [Fact]
        public void Cppro_Start_FailsWhenTheWriteFails()
        {
            // A failed send marks the keyboard disconnected (AnalogueKeyboard.cpp:1280-1284).
            var io = new AnalogKeyboardTestTransport { Gone = true };
            Assert.False(new CenterpieceProSession(() => 0).Start(io));
        }

        [Fact]
        public void Cppro_Pass_UpdatesOneKeyPerReport_AndKeepsTheOthers()
        {
            // One key per report into a per-key buffer
            // (AnalogueKeyboard.cpp:1288-1320). H14 is W and H28 is A
            // (CpproProtocol.hpp:17-36).
            var io = new AnalogKeyboardTestTransport();
            long now = 0;
            var session = new CenterpieceProSession(() => now);
            session.Start(io);
            var output = Keys();

            io.QueueInput(CpproKey(14, 20));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));

            io.QueueInput(CpproKey(28, 40, type: 1));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.A));
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));

            // Travel 0 releases the key (normalizeDistance, CpproProtocol.hpp:45-49).
            io.QueueInput(CpproKey(14, 0, type: 2));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.A));
            Assert.Equal(1, output.Count);
        }

        [Fact]
        public void Cppro_Depth_ClampsPast40()
        {
            // normalizeDistance clamps at 40 (CpproProtocol.hpp:45-49).
            Assert.Equal(0f, CenterpieceProProtocol.Depth(0));
            Assert.Equal(1f / 40f, CenterpieceProProtocol.Depth(1), 6);
            Assert.Equal(0.5f, CenterpieceProProtocol.Depth(20));
            Assert.Equal(1f, CenterpieceProProtocol.Depth(40));
            Assert.Equal(1f, CenterpieceProProtocol.Depth(255));
        }

        [Fact]
        public void Cppro_Pass_SkipsWhatTheDecoderRejects()
        {
            // decodeKeyReport's checks (CpproProtocol.hpp:53-66): length 8,
            // report 4, event type 1 to 3, an index the table maps.
            var io = new AnalogKeyboardTestTransport();
            var session = new CenterpieceProSession(() => 0);
            session.Start(io);
            var output = Keys();
            output.Set(AnalogKeyCodes.Q, 0.25f);

            var rejects = new List<byte[]>
            {
                CpproKey(14, 20, length: 7),
                CpproKey(14, 20, type: 0),
                CpproKey(14, 20, type: 4),
                CpproKey(0, 20),
                CpproKey(69, 20),
                CpproKey(200, 20),
            };
            var wrongId = CpproKey(14, 20);
            wrongId[0] = 0x05;
            rejects.Add(wrongId);

            foreach (var report in rejects)
            {
                io.QueueInput(report);
                Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
            }
            Assert.Equal(1, output.Count);
            Assert.Equal(0.25f, output.Get(AnalogKeyCodes.Q));
        }

        [Fact]
        public void Cppro_Pass_RepeatsTheRequestEvery2500Ms()
        {
            // KEY_REPORT_REFRESH_MS (CpproProtocol.hpp:13), the next request
            // counted from the last (AnalogueKeyboard.cpp:1275-1286).
            var io = new AnalogKeyboardTestTransport();
            long now = 1000;
            var session = new CenterpieceProSession(() => now);
            session.Start(io);
            var output = Keys();

            now = 3499;
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
            Assert.Single(io.Writes("out"));

            now = 3500;
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
            Assert.Equal(2, io.Writes("out").Count);
            Assert.Equal(6000, session.NextRequestAt);
            Assert.All(io.Writes("out"), w => Assert.Equal(CenterpieceProProtocol.Request(), w));
        }

        [Fact]
        public void Cppro_Pass_FailsWhenTheKeyboardIsGone()
        {
            var io = new AnalogKeyboardTestTransport();
            long now = 0;
            var session = new CenterpieceProSession(() => now);
            session.Start(io);
            io.Gone = true;
            // The read fails before the refresh is due.
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, Keys(), null));
            // And the refresh write fails once it is due.
            now = 5000;
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, Keys(), null));
        }

        [Fact]
        public void Cppro_Stop_WritesNothing()
        {
            // The fork calls the request read-only and sends nothing to end it
            // (AnalogueKeyboard.cpp:1273).
            var io = new AnalogKeyboardTestTransport();
            var session = new CenterpieceProSession(() => 0);
            session.Start(io);
            int before = io.Log.Count;
            session.Stop(io);
            Assert.Equal(before, io.Log.Count);
        }

        [Fact]
        public void Cppro_Table_IsTheForksKeyLayout()
        {
            // KEY_LAYOUT (CpproProtocol.hpp:16-37): index 0 unused, H1 is 2,
            // H4 Enter, H59 Fn, H68 Escape, 68 distinct keys.
            var table = CenterpieceProProtocol.Table();
            Assert.Equal(69, table.Length);
            Assert.Equal(0, table[0]);
            Assert.Equal(AnalogKeyCodes.D2, table[1]);
            Assert.Equal(AnalogKeyCodes.Enter, table[4]);
            Assert.Equal(AnalogKeyCodes.RShift, table[9]);
            Assert.Equal(AnalogKeyCodes.Delete, table[33]);
            Assert.Equal(AnalogKeyCodes.ArrowLeft, table[40]);
            Assert.Equal(AnalogKeyCodes.Fn, table[59]);
            Assert.Equal(AnalogKeyCodes.LMeta, table[63]);
            Assert.Equal(AnalogKeyCodes.Escape, table[68]);
            Assert.Equal(68, table.Skip(1).Distinct().Count(c => c != 0));

            var order = new CenterpieceProSession(() => 0).KeyOrder;
            Assert.Equal(68, order.Length);
            Assert.Equal(AnalogKeyCodes.D2, order[0]);
            Assert.Equal(AnalogKeyCodes.Escape, order[67]);
        }

        // ── libhmk ─────────────────────────────────────────────────────────

        private static AnalogKeyboardDeviceInfo LibhmkInfo(ushort pid, ushort vid = 0xAB50, ushort usagePage = 0xFFAB,
            ushort usage = 0x00AB, ushort length = 65, bool numbered = false)
        {
            byte[] ids = numbered ? new byte[] { 1 } : new byte[] { 0 };
            return Info(vid, pid, usagePage, usage, length, length, ids, ids);
        }

        // libhmk keycodes (keycodes.h:20-205).
        private const byte KC_NO = 0x00, KC_TRNS = 0x01, KC_A = 0x02, KC_B = 0x03, KC_C = 0x04, KC_D = 0x05,
            KC_E = 0x06, KC_F = 0x07, KC_G = 0x08, KC_H = 0x09, KC_I = 0x0A, KC_J = 0x0B, KC_K = 0x0C,
            KC_L = 0x0D, KC_M = 0x0E, KC_N = 0x0F, KC_O = 0x10, KC_P = 0x11, KC_Q = 0x12, KC_R = 0x13,
            KC_S = 0x14, KC_T = 0x15, KC_U = 0x16, KC_V = 0x17, KC_W = 0x18, KC_X = 0x19, KC_Y = 0x1A,
            KC_Z = 0x1B, KC_1 = 0x1C, KC_2 = 0x1D, KC_3 = 0x1E, KC_4 = 0x1F, KC_5 = 0x20, KC_6 = 0x21,
            KC_7 = 0x22, KC_8 = 0x23, KC_9 = 0x24, KC_0 = 0x25, KC_ENT = 0x26, KC_ESC = 0x27, KC_BSPC = 0x28,
            KC_TAB = 0x29, KC_SPC = 0x2A, KC_MINS = 0x2B, KC_EQL = 0x2C, KC_LBRC = 0x2D, KC_RBRC = 0x2E,
            KC_BSLS = 0x2F, KC_NUHS = 0x30, KC_SCLN = 0x31, KC_QUOT = 0x32, KC_COMM = 0x34, KC_DOT = 0x35,
            KC_SLSH = 0x36, KC_CAPS = 0x37, KC_F24 = 0x6F, KC_NUBS = 0x62, KC_APP = 0x63, KC_INT1 = 0x70,
            KC_LNG5 = 0x7A, KC_LCTL = 0x7B, KC_LSFT = 0x7C, KC_LALT = 0x7D, KC_LGUI = 0x7E, KC_RCTL = 0x7F,
            KC_RSFT = 0x80, KC_RALT = 0x81, KC_RGUI = 0x82, KC_PWR = 0x83, KC_MUTE = 0x86, KC_MPLY = 0x8C,
            KC_MAIL = 0x8E, KC_BRIU = 0x97, MS_BTN1 = 0x9B, MO_1 = 0xC1, MO_2 = 0xC2, PF_0 = 0xC8;

        /// <summary>The HE60 v2's default layer 0 (keyboards/he60-v2/keyboard.json:148-218).</summary>
        private static readonly byte[] He60V2Layer0 =
        {
            KC_ESC, KC_1, KC_2, KC_3, KC_4, KC_5, KC_6, KC_7, KC_8, KC_9, KC_0, KC_MINS, KC_EQL, KC_TRNS,
            KC_BSPC, KC_TRNS, KC_TAB, KC_Q, KC_W, KC_E, KC_R, KC_T, KC_Y, KC_U, KC_I, KC_O, KC_P, KC_LBRC,
            KC_RBRC, KC_BSLS, KC_CAPS, KC_A, KC_S, KC_D, KC_F, KC_G, KC_H, KC_J, KC_K, KC_L, KC_SCLN,
            KC_QUOT, KC_ENT, KC_LSFT, KC_Z, KC_X, KC_C, KC_V, KC_B, KC_N, KC_M, KC_COMM, KC_DOT, KC_SLSH,
            KC_TRNS, KC_RSFT, KC_TRNS, KC_LCTL, KC_LGUI, KC_LALT, KC_TRNS, KC_TRNS, KC_SPC, KC_TRNS,
            KC_TRNS, KC_RALT, MO_1, KC_APP, KC_RCTL,
        };

        private static byte[] Gzip(string json)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                gz.Write(bytes, 0, bytes.Length);
            }
            return ms.ToArray();
        }

        /// <summary>Metadata shaped as scripts/metadata.py:50-73 writes it.</summary>
        private static string MetadataJson(string name = "HE60", int keys = 69, int profiles = 4, int layers = 4)
            => "{\"name\": \"" + name + "\", \"vendorId\": \"0xAB50\", \"productId\": \"0xAB60\", "
               + "\"usbHighSpeed\": true, \"adcResolution\": 12, \"numProfiles\": " + profiles
               + ", \"numLayers\": " + layers + ", \"numKeys\": " + keys + ", \"numAdvancedKeys\": 32, "
               + "\"numDynamicKeystrokeMaxBindings\": 4, \"numMacroNodes\": 128, "
               + "\"layout\": {\"labels\": [\"Split Backspace\", \"Split R-Shift\"], \"keymap\": ["
               + string.Join(", ", Enumerable.Range(0, 5).Select(r => "[" + string.Join(", ",
                   Enumerable.Range(0, 14).Select(c => "{\"key\": " + (r * 14 + c) + "}")) + "]"))
               + "]}, \"defaultKeymaps\": []}";

        /// <summary>
        /// A libhmk keyboard as commands.c answers: one 64-byte answer per
        /// request, the command echoed in byte 0 or 255 on failure, written
        /// into one buffer that is never cleared between answers
        /// (commands.c:38 and 187-481).
        /// </summary>
        private sealed class FakeLibhmk
        {
            public ushort Version = 0x0108;
            public byte[] Metadata = Gzip(MetadataJson());
            public int NumKeys = 69;
            public int NumProfiles = 4;
            public int Profile = 1;
            public byte[][] Layer0 = new byte[4][];
            public byte[] Distance = new byte[256];
            public ushort[] Adc = new ushort[256];
            public Func<byte[], bool> Silent = _ => false;
            public readonly byte[] OutBuf = new byte[64];
            public readonly List<byte[]> Requests = new();

            public FakeLibhmk()
            {
                for (int p = 0; p < 4; p++) Layer0[p] = new byte[NumKeys];
                Layer0[1] = (byte[])He60V2Layer0.Clone();
            }

            public IEnumerable<byte[]> Respond(byte[] request)
            {
                Requests.Add(request);
                if (Silent(request)) return Array.Empty<byte[]>();
                byte command = request[1];
                var o = OutBuf;
                bool ok = true;
                switch (command)
                {
                    case 0:
                        o[1] = (byte)Version;
                        o[2] = (byte)(Version >> 8);
                        break;
                    case 5:
                    {
                        int offset = request[2];
                        if (offset >= NumKeys) { ok = false; break; }
                        for (int i = 0; i < 21 && i + offset < NumKeys; i++)
                        {
                            o[1 + 3 * i] = (byte)Adc[offset + i];
                            o[2 + 3 * i] = (byte)(Adc[offset + i] >> 8);
                            o[3 + 3 * i] = Distance[offset + i];
                        }
                        break;
                    }
                    case 8:
                        o[1] = (byte)Profile;
                        break;
                    case 13:
                    {
                        int offset = request[2] | (request[3] << 8) | (request[4] << 16) | (request[5] << 24);
                        if (offset >= Metadata.Length) { ok = false; break; }
                        int left = Metadata.Length - offset;
                        o[1] = (byte)left;
                        o[2] = (byte)(left >> 8);
                        o[3] = (byte)(left >> 16);
                        o[4] = (byte)(left >> 24);
                        Array.Copy(Metadata, offset, o, 5, Math.Min(59, left));
                        break;
                    }
                    case 128:
                    {
                        int profile = request[2], layer = request[3], offset = request[4];
                        if (profile >= NumProfiles || layer >= 4 || offset >= NumKeys) { ok = false; break; }
                        byte[] map = layer == 0 ? Layer0[profile] : new byte[NumKeys];
                        Array.Copy(map, offset, o, 1, Math.Min(63, NumKeys - offset));
                        break;
                    }
                    default:
                        ok = false;
                        break;
                }
                o[0] = ok ? command : (byte)255;
                var answer = new byte[65];
                Array.Copy(o, 0, answer, 1, 64);
                return new[] { answer };
            }
        }

        private static (AnalogKeyboardTestTransport io, FakeLibhmk kb) LibhmkDevice()
        {
            var kb = new FakeLibhmk();
            var io = new AnalogKeyboardTestTransport { OnSend = kb.Respond };
            return (io, kb);
        }

        [Fact]
        public void Libhmk_Matches_TheRawHidCollection_AndRejectsNearMisses()
        {
            // keyboards/*/keyboard.json:6-7 and usb_descriptors.h:103-107:
            // AB50 with AB16, AB60 or AB65, FFAB:AB, 64-byte reports, no ID.
            Assert.True(LibhmkProtocol.Matches(LibhmkInfo(0xAB16)));
            Assert.True(LibhmkProtocol.Matches(LibhmkInfo(0xAB60)));
            Assert.True(LibhmkProtocol.Matches(LibhmkInfo(0xAB65)));
            Assert.False(LibhmkProtocol.Matches(LibhmkInfo(0xAB61)));
            Assert.False(LibhmkProtocol.Matches(LibhmkInfo(0xAB60, vid: 0xAB51)));
            Assert.False(LibhmkProtocol.Matches(LibhmkInfo(0xAB60, usagePage: 0xFF60, usage: 0x61)));
            Assert.False(LibhmkProtocol.Matches(LibhmkInfo(0xAB60, usage: 0x00AC)));
            Assert.False(LibhmkProtocol.Matches(LibhmkInfo(0xAB60, length: 33)));
            Assert.False(LibhmkProtocol.Matches(LibhmkInfo(0xAB60, numbered: true)));
            Assert.False(LibhmkProtocol.Matches(null));
        }

        [Fact]
        public void Libhmk_Requests_AreHmkconfsFrames()
        {
            // Report ID 0, then command_in_buffer_t (commands.h:134-151) as
            // hmkconf's Commander lays it out (commander.ts:56-60).
            Assert.Equal(Report(65, 0x00, 0x00), LibhmkProtocol.FirmwareVersionRequest());
            Assert.Equal(Report(65, 0x00, 0x0D, 0x04, 0x03, 0x02, 0x01), LibhmkProtocol.MetadataRequest(0x01020304));
            Assert.Equal(Report(65, 0x00, 0x08), LibhmkProtocol.ProfileRequest());
            Assert.Equal(Report(65, 0x00, 0x80, 0x01, 0x00, 0x3F), LibhmkProtocol.KeymapRequest(1, 0, 63));
            Assert.Equal(Report(65, 0x00, 0x05, 0x2A), LibhmkProtocol.AnalogInfoRequest(42));
        }

        [Fact]
        public void Libhmk_Start_ReadsVersionMetadataProfileAndLayer0()
        {
            // hmkconf's connect (hmk-keyboard.svelte.ts:240-249), then the
            // profile and keymap reads (profile.ts:21-25, keymap.ts:24-48).
            var (io, kb) = LibhmkDevice();
            var session = new LibhmkSession();
            Assert.True(session.Start(io));

            Assert.Equal(0x0108, session.FirmwareVersion);
            Assert.Equal("HE60", session.ModelName);
            Assert.Equal(69, session.Metadata.NumKeys);
            Assert.Equal(1, session.Profile);

            // Version, the metadata in 59-byte steps, profile, keymap pages.
            int chunks = (kb.Metadata.Length + 58) / 59;
            var commands = kb.Requests.Select(r => r[1]).ToArray();
            var expected = new List<byte> { 0 };
            expected.AddRange(Enumerable.Repeat((byte)13, chunks));
            expected.Add(8);
            expected.Add(128);
            expected.Add(128);
            Assert.Equal(expected.ToArray(), commands);
            for (int c = 0; c < chunks; c++)
                Assert.Equal(LibhmkProtocol.MetadataRequest(c * 59), kb.Requests[1 + c]);
            Assert.Equal(LibhmkProtocol.KeymapRequest(1, 0, 0), kb.Requests[chunks + 2]);
            Assert.Equal(LibhmkProtocol.KeymapRequest(1, 0, 63), kb.Requests[chunks + 3]);
            // Each request is preceded by a stale-report discard.
            Assert.Equal(kb.Requests.Count, io.Discards);

            var codes = session.Codes;
            Assert.Equal(69, codes.Length);
            Assert.Equal(AnalogKeyCodes.Escape, codes[0]);
            Assert.Equal(AnalogKeyCodes.W, codes[18]);
            Assert.Equal(AnalogKeyCodes.LShift, codes[43]);
            Assert.Equal(AnalogKeyCodes.Space, codes[62]);
            Assert.Equal(AnalogKeyCodes.Fn, codes[66]);           // MO(1)
            Assert.Equal(AnalogKeyCodes.ContextMenu, codes[67]);
            Assert.Equal(AnalogKeyCodes.RCtrl, codes[68]);        // from the second keymap page
            Assert.Equal(0x600 + 13, codes[13]);                  // transparent
            Assert.Equal(0x600 + 15, codes[15]);
            Assert.Equal(69, codes.Distinct().Count());
            Assert.Equal(codes, session.KeyOrder);
        }

        [Fact]
        public void Libhmk_Start_RejectsFirmwareOlderThanHmkconfAccepts()
        {
            // HMK_FIRMWARE_MIN_VERSION (hmkconf libhmk/index.ts:19, hmk-keyboard.svelte.ts:241-246).
            var (io, kb) = LibhmkDevice();
            kb.Version = 0x0103;
            Assert.False(new LibhmkSession().Start(io));
            Assert.Single(kb.Requests);

            var (io2, kb2) = LibhmkDevice();
            kb2.Version = 0x0104;
            Assert.True(new LibhmkSession().Start(io2));
        }

        [Fact]
        public void Libhmk_Start_RejectsARefusal()
        {
            // 255 answers a failed command (commands.c:479-480).
            var (io, kb) = LibhmkDevice();
            kb.Metadata = new byte[0];
            Assert.False(new LibhmkSession().Start(io));
            Assert.Equal(2, kb.Requests.Count);
        }

        [Fact]
        public void Libhmk_Start_RejectsMetadataOutsideHmkconfsSchema()
        {
            // keyboardMetadataSchema (hmkconf keyboard/metadata.ts:96-118).
            var bad = new[]
            {
                Encoding.UTF8.GetBytes(MetadataJson()),              // not gzip
                Gzip("{\"name\": \"HE60\""),                         // not JSON
                Gzip(MetadataJson(keys: 0)),
                Gzip(MetadataJson(keys: 257)),
                Gzip(MetadataJson(profiles: 9)),
                Gzip(MetadataJson(layers: 0)),
                Gzip(MetadataJson().Replace("\"name\": \"HE60\", ", "")),
            };
            foreach (var metadata in bad)
            {
                var (io, kb) = LibhmkDevice();
                kb.Metadata = metadata;
                Assert.False(new LibhmkSession().Start(io));
            }
        }

        [Fact]
        public void Libhmk_Start_RejectsAProfilePastTheCount()
        {
            // GET_KEYMAP verifies the profile (commands.c:277), and so does
            // the handshake before asking.
            var (io, kb) = LibhmkDevice();
            kb.Metadata = Gzip(MetadataJson(profiles: 1));
            Assert.False(new LibhmkSession().Start(io));
            Assert.DoesNotContain(kb.Requests, r => r[1] == 128);
        }

        [Fact]
        public void Libhmk_Start_FailsWhenTheKeyboardIsSilentOrGone()
        {
            // hmkconf gives up on a command that is not answered in time
            // (commander.ts:84-87) and on a failed write (commander.ts:65-67).
            var (io, kb) = LibhmkDevice();
            kb.Silent = r => r[1] == 8;
            Assert.False(new LibhmkSession().Start(io));

            var gone = new AnalogKeyboardTestTransport { Gone = true };
            Assert.False(new LibhmkSession().Start(gone));
        }

        [Fact]
        public void Libhmk_Pass_ReadsEveryKeyIn21EntryGroups()
        {
            // analogInfo (hmkconf analog-info.ts:23-45) against
            // COMMAND_ANALOG_INFO (commands.c:215-227): entries of a u16 ADC
            // value and a u8 distance.
            var (io, kb) = LibhmkDevice();
            var session = new LibhmkSession();
            Assert.True(session.Start(io));
            kb.Requests.Clear();

            kb.Distance[18] = 255;   // W
            kb.Distance[31] = 128;   // A
            kb.Distance[66] = 64;    // MO(1), Fn
            kb.Distance[68] = 3;     // RCtrl, below HMK_MIN_DISTANCE
            kb.Adc[18] = 0x0C41;

            var output = Keys();
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(new[] { 0, 21, 42, 63 }, kb.Requests.Select(r => (int)r[2]).ToArray());
            Assert.All(kb.Requests, r => Assert.Equal(LibhmkProtocol.CommandAnalogInfo, r[1]));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(128 / 255f, output.Get(AnalogKeyCodes.A), 6);
            Assert.Equal(64 / 255f, output.Get(AnalogKeyCodes.Fn), 6);
            Assert.Equal(0f, output.Get(AnalogKeyCodes.RCtrl));
            Assert.Equal(3, output.Count);

            // The next pass replaces the set.
            kb.Distance[18] = 0;
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(2, output.Count);
        }

        [Fact]
        public void Libhmk_Pass_ReadsNoEntryPastTheKeyCount()
        {
            // Entries past NUM_KEYS keep the previous answer's bytes
            // (commands.c:221-225 writes only the keys that exist), so the
            // last group is read only up to the key count (analog-info.ts:36).
            var (io, kb) = LibhmkDevice();
            var session = new LibhmkSession();
            Assert.True(session.Start(io));
            for (int i = 42; i < 63; i++) kb.Distance[i] = 200;
            var output = Keys();
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            // Keys 42 to 62 are down, the transparent ones under their
            // vendor codes. The group at 63 carries only keys 63 to 68, and
            // its stale tail repeats the 200s without effect.
            Assert.Equal(21, output.Count);
            Assert.Equal(200 / 255f, output.Get(AnalogKeyCodes.Enter), 6);
            Assert.Equal(200 / 255f, output.Get(0x600 + 54), 6);
            Assert.Equal(0f, output.Get(AnalogKeyCodes.RAlt));
            Assert.Equal(0f, output.Get(AnalogKeyCodes.RCtrl));
        }

        [Fact]
        public void Libhmk_Pass_RefusalFails_SilenceIsNoAnswer_GoneFails()
        {
            var (io, kb) = LibhmkDevice();
            var session = new LibhmkSession();
            Assert.True(session.Start(io));

            // A refused offset means the key count is wrong (commands.c:219).
            kb.NumKeys = 21;
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, Keys(), null));

            kb.NumKeys = 69;
            kb.Silent = r => r[1] == 5 && r[2] == 42;
            Assert.Equal(AnalogPollResult.NoAnswer, session.Pass(io, Keys(), null));

            kb.Silent = _ => false;
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, Keys(), null));

            io.Gone = true;
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, Keys(), null));
        }

        [Fact]
        public void Libhmk_ALateAnswer_IsWaitedOut_BeforeTheNextRequest()
        {
            // An answer names only its command (commands.c:479-480), so after a
            // request times out the next exchange first reads until the late
            // answer arrives, or hmkconf's 4 s timeout from the request runs
            // out (commander.ts:48), before it sends. The late answer cannot
            // pass for the next request's.
            var (io, kb) = LibhmkDevice();
            var session = new LibhmkSession();
            Assert.True(session.Start(io));
            kb.Distance[18] = 255;   // W, in offset 0's group
            kb.Silent = r => r[1] == LibhmkProtocol.CommandAnalogInfo && r[2] == 42;
            Assert.Equal(AnalogPollResult.NoAnswer, session.Pass(io, Keys(), null));

            kb.Silent = _ => false;
            var late = new byte[65];
            late[1] = LibhmkProtocol.CommandAnalogInfo;
            late[4] = 200;           // the first entry's distance
            io.QueueInput(late);
            var ordered = new OrderedTransport(io);
            var output = Keys();
            Assert.Equal(AnalogPollResult.Ok, session.Pass(ordered, output, null));
            Assert.Equal("recv", ordered.Ops[0]);
            Assert.Equal(0, io.PendingInput);
            Assert.Equal(1f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(1, output.Count);

            // With nothing late, a pass starts with its own request.
            var next = new OrderedTransport(io);
            Assert.Equal(AnalogPollResult.Ok, session.Pass(next, Keys(), null));
            Assert.Equal("discard", next.Ops[0]);
        }

        /// <summary>Forwards to the scripted transport and logs the order of
        /// reads, writes and discards.</summary>
        private sealed class OrderedTransport : IAnalogKeyboardTransport
        {
            private readonly AnalogKeyboardTestTransport _inner;
            public readonly List<string> Ops = new();

            public OrderedTransport(AnalogKeyboardTestTransport inner) => _inner = inner;

            public bool Send(byte[] report)
            {
                Ops.Add("send");
                return _inner.Send(report);
            }

            public bool SendOutputReport(byte[] report)
            {
                Ops.Add("send");
                return _inner.SendOutputReport(report);
            }

            public int Receive(byte[] buffer, int timeoutMs)
            {
                Ops.Add("recv");
                return _inner.Receive(buffer, timeoutMs);
            }

            public void DiscardStale()
            {
                Ops.Add("discard");
                _inner.DiscardStale();
            }

            public bool SetFeature(byte[] report) => _inner.SetFeature(report);
            public int GetFeature(byte[] buffer) => _inner.GetFeature(buffer);
            public int InputLength => _inner.InputLength;
            public int OutputLength => _inner.OutputLength;
            public int FeatureLength => _inner.FeatureLength;
        }

        [Fact]
        public void Libhmk_Pass_SkipsAnswersToOtherCommands()
        {
            // hmkconf's Commander drops answers whose byte 0 is not the
            // command it sent (commander.ts:69-77).
            var (io, kb) = LibhmkDevice();
            var session = new LibhmkSession();
            Assert.True(session.Start(io));
            kb.Distance[18] = 255;
            var respond = io.OnSend;
            io.OnSend = request =>
            {
                var answers = new List<byte[]> { Report(65, 0x00, 0x80, 0x29) };
                answers.AddRange(respond(request));
                return answers;
            };
            var output = Keys();
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.W));
        }

        [Fact]
        public void Libhmk_Stop_WritesNothing()
        {
            // Every command the route sends is a read (commands.c:193-296).
            var (io, _) = LibhmkDevice();
            var session = new LibhmkSession();
            Assert.True(session.Start(io));
            int before = io.Log.Count;
            session.Stop(io);
            Assert.Equal(before, io.Log.Count);
        }

        [Fact]
        public void Libhmk_KeycodeTable_FollowsKeycodeToHid()
        {
            // keycode_to_hid (keycodes.c:20-191) with TinyUSB 0.20.0's usage
            // values. Modifier bit n is usage 0xE0 + n in libhmk's keyboard
            // descriptor (usb_descriptors.c:53-57). Consumer usages up to
            // 0xFF take the 0x3xx namespace.
            var t = AnalogKeyboardData.Table(OtherRoutes.DataFile, LibhmkProtocol.KeycodeTableName);
            Assert.Equal(256, t.Length);
            Assert.Equal(0, t[KC_NO]);
            Assert.Equal(0, t[KC_TRNS]);
            Assert.Equal(AnalogKeyCodes.A, t[KC_A]);
            Assert.Equal(AnalogKeyCodes.Enter, t[KC_ENT]);
            Assert.Equal(AnalogKeyCodes.Escape, t[KC_ESC]);
            Assert.Equal(AnalogKeyCodes.IntlHash, t[KC_NUHS]);
            Assert.Equal(AnalogKeyCodes.IntlBackslash, t[KC_NUBS]);
            Assert.Equal(AnalogKeyCodes.ContextMenu, t[KC_APP]);
            Assert.Equal(AnalogKeyCodes.F24, t[KC_F24]);
            Assert.Equal(0x87, t[KC_INT1]);
            Assert.Equal(0x94, t[KC_LNG5]);
            Assert.Equal(AnalogKeyCodes.LCtrl, t[KC_LCTL]);
            Assert.Equal(AnalogKeyCodes.RMeta, t[KC_RGUI]);
            Assert.Equal(0, t[KC_PWR]);
            Assert.Equal(AnalogKeyCodes.ConsumerMute, t[KC_MUTE]);
            Assert.Equal(AnalogKeyCodes.PlayPause, t[KC_MPLY]);
            Assert.Equal(0, t[KC_MAIL]);        // consumer usage 0x18A has no 0x3xx code
            Assert.Equal(0x36F, t[KC_BRIU]);
            Assert.Equal(0, t[MS_BTN1]);
            Assert.Equal(0, t[MO_1]);
            Assert.Equal(0, t[PF_0]);
        }

        [Fact]
        public void Libhmk_KeyCodes_GiveEveryKeyItsOwnCode()
        {
            // keycode_to_hid has no entry for MO, transparent, KC_NO or the
            // 0x18A mail usage (keycodes.c:20-191). The first MO key is Fn,
            // the rest and every repeat take 0x600 plus their index.
            var table = AnalogKeyboardData.Table(OtherRoutes.DataFile, LibhmkProtocol.KeycodeTableName);
            var keymap = new byte[] { KC_A, KC_A, MO_1, MO_2, KC_TRNS, KC_NO, KC_MAIL, KC_B };
            Assert.Equal(new[]
            {
                AnalogKeyCodes.A, 0x601, AnalogKeyCodes.Fn, 0x603, 0x604, 0x605, 0x606, AnalogKeyCodes.B,
            }, LibhmkProtocol.KeyCodes(keymap, table));
        }

        [Fact]
        public void Libhmk_Depth_IsDistanceOver255_WithRestBelow4()
        {
            // HMK_MIN_DISTANCE and HMK_MAX_DISTANCE (hmkconf libhmk/index.ts:39-40).
            Assert.Equal(0f, LibhmkProtocol.Depth(0));
            Assert.Equal(0f, LibhmkProtocol.Depth(3));
            Assert.Equal(4 / 255f, LibhmkProtocol.Depth(4), 6);
            Assert.Equal(1f, LibhmkProtocol.Depth(255));
        }

        [Fact]
        public void Libhmk_Metadata_ReadsTheFirmwaresGzipJson()
        {
            // keyboard_metadata_def (scripts/metadata.py:50-73).
            Assert.True(LibhmkProtocol.TryParseMetadata(Gzip(MetadataJson("M256-WHE", 68)), out var metadata));
            Assert.Equal("M256-WHE", metadata.Name);
            Assert.Equal(68, metadata.NumKeys);
            Assert.Equal(4, metadata.NumProfiles);
            Assert.Equal(4, metadata.NumLayers);
            Assert.False(LibhmkProtocol.TryParseMetadata(new byte[] { 0x1F, 0x8B, 0x08 }, out _));
        }

        // ── ROG Azoth 96 HE ────────────────────────────────────────────────

        private static AnalogKeyboardDeviceInfo AzothEvents(ushort pid = 0x1C10, ushort input = 21, ushort output = 0,
            byte reportId = 3, ushort usagePage = 0xFFC0)
            => Info(0x0B05, pid, usagePage, 0x0001, input, output, new[] { reportId });

        private static AnalogKeyboardDeviceInfo AzothControl(ushort pid = 0x1C10, ushort length = 64,
            ushort usagePage = 0xFF00, bool numbered = false, AnalogKeyboardDeviceInfo[] siblings = null)
        {
            byte[] ids = numbered ? new byte[] { 1 } : new byte[] { 0 };
            var info = Info(0x0B05, pid, usagePage, 0x0001, length, length, ids, ids);
            info.Siblings = siblings ?? new[] { AzothEvents(pid) };
            return info;
        }

        /// <summary>The keyboard's ordinary keyboard collection, a sibling
        /// no route reads.</summary>
        private static AnalogKeyboardDeviceInfo AzothKeyboard() => Info(0x0B05, 0x1C10, 0x0001, 0x0006, 9, 2);

        /// <summary>A travel event, report 3 then <c>7E</c>, the key and the
        /// travel little-endian, 21 bytes (backend:34 and 223-229).</summary>
        private static byte[] AzothTravel(int key, int travel, int length = 21)
        {
            var r = new byte[length];
            r[0] = 0x03;
            r[1] = 0x7E;
            r[2] = (byte)key;
            r[3] = (byte)(key >> 8);
            r[4] = (byte)travel;
            r[5] = (byte)(travel >> 8);
            return r;
        }

        [Fact]
        public void Azoth_Matches_TheControlCollectionBesideItsEventCollection()
        {
            // HallJoy's exact pair (backend:28-35, 132-183, 185-211).
            Assert.True(RogAzoth96HeProtocol.Matches(AzothControl()));
            Assert.True(RogAzoth96HeProtocol.Matches(AzothControl(length: 65)));
            // Both interfaces must be present.
            Assert.False(RogAzoth96HeProtocol.Matches(AzothControl(siblings: new[] { AzothKeyboard() })));
            Assert.False(RogAzoth96HeProtocol.Matches(AzothControl(siblings: new[] { AzothEvents(input: 20) })));
            Assert.False(RogAzoth96HeProtocol.Matches(AzothControl(siblings: new[] { AzothEvents(output: 21) })));
            Assert.False(RogAzoth96HeProtocol.Matches(AzothControl(siblings: new[] { AzothEvents(reportId: 4) })));
            Assert.False(RogAzoth96HeProtocol.Matches(AzothControl(siblings: new[] { AzothEvents(usagePage: 0xFFC1) })));
            Assert.False(RogAzoth96HeProtocol.Matches(AzothControl(siblings: new[] { AzothEvents(pid: 0x1C12) })));
            // Wired only (recon:25-38).
            Assert.False(RogAzoth96HeProtocol.Matches(AzothControl(pid: 0x1C11)));
            Assert.False(RogAzoth96HeProtocol.Matches(AzothControl(pid: 0x1C12)));
            Assert.False(RogAzoth96HeProtocol.Matches(AzothControl(pid: 0x1C13)));
            Assert.False(RogAzoth96HeProtocol.Matches(AzothControl(usagePage: 0xFF02)));
            Assert.False(RogAzoth96HeProtocol.Matches(AzothControl(length: 33)));
            Assert.False(RogAzoth96HeProtocol.Matches(AzothControl(numbered: true)));
            // The event collection itself is not the route's to command.
            Assert.False(RogAzoth96HeProtocol.Matches(AzothEvents()));
        }

        [Fact]
        public void Azoth_Companion_IsTheSameKeyboardsEventCollection()
        {
            // HallJoy reads the FFC0 collection beside the FF00 one
            // (backend:252-265). Here it is the same keyboard's sibling.
            var events = AzothEvents();
            var control = AzothControl(siblings: new[] { AzothKeyboard(), events });
            Assert.Same(events, OtherRoutes.All[2].Companion(control));
            Assert.Null(RogAzoth96HeProtocol.FindEvents(new AnalogKeyboardDeviceInfo()));
        }

        [Fact]
        public void Azoth_Start_SendsTheEnableAsAnOutputReport()
        {
            // SendEnableTravel: HidD_SetOutputReport with 51 61 00 00
            // (backend:213-221, recon:67-74), report ID 0 first. Never 80 26
            // (recon:87-90).
            var io = new AnalogKeyboardTestTransport();
            var session = new RogAzoth96HeSession();
            Assert.True(session.Start(io));
            Assert.True(session.EnableSent);
            Assert.Single(io.Log);
            Assert.Equal(new byte[] { 0x00, 0x51, 0x61, 0x00, 0x00 }, io.Writes("ctl")[0]);
            Assert.DoesNotContain(io.Log, w => w.Data.Length > 2 && w.Data[1] == 0x80 && w.Data[2] == 0x26);
            Assert.Equal("ROG Azoth 96 HE", session.ModelName);
        }

        [Fact]
        public void Azoth_Start_KeepsReadingWhenTheEnableFails()
        {
            // HallJoy logs a failed enable and reads on (backend:280-289).
            var io = new AnalogKeyboardTestTransport { Gone = true };
            var session = new RogAzoth96HeSession();
            Assert.True(session.Start(io));
            Assert.False(session.EnableSent);
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, Keys(), null));
        }

        [Fact]
        public void Azoth_Pass_ReadsTravelEvents()
        {
            // RecordTravel (backend:223-229). Travel in 0.01 mm, 3.50 mm deep
            // (recon:42-45). No key map exists, so firmware key 0x23 is 0x623.
            var io = new AnalogKeyboardTestTransport();
            var session = new RogAzoth96HeSession();
            session.Start(io);
            var output = Keys();

            io.QueueInput(AzothTravel(0x23, 175));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.5f, output.Get(0x623));

            io.QueueInput(AzothTravel(0x05, 350));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(1f, output.Get(0x605));
            Assert.Equal(0.5f, output.Get(0x623));

            io.QueueInput(AzothTravel(0x05, 400));
            session.Pass(io, output, null);
            Assert.Equal(1f, output.Get(0x605));

            io.QueueInput(AzothTravel(0x23, 0));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0f, output.Get(0x623));
            Assert.Equal(1, output.Count);
        }

        [Fact]
        public void Azoth_Pass_SkipsOtherReports()
        {
            // A read of another length is skipped (backend:300-302), and so
            // is another report ID or event (backend:225). A firmware key
            // past 0xFF has no vendor code.
            var io = new AnalogKeyboardTestTransport();
            var session = new RogAzoth96HeSession();
            session.Start(io);
            var output = Keys();
            output.Set(0x610, 0.25f);

            var wrongId = AzothTravel(0x23, 175);
            wrongId[0] = 0x04;
            var wrongEvent = AzothTravel(0x23, 175);
            wrongEvent[1] = 0x7F;
            foreach (var report in new[] { AzothTravel(0x23, 175, length: 20), wrongId, wrongEvent, AzothTravel(0x123, 175) })
            {
                io.QueueInput(report);
                Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
            }
            Assert.Equal(1, output.Count);
            Assert.Equal(0.25f, output.Get(0x610));
        }

        [Fact]
        public void Azoth_Pass_SilenceIsIdle_GoneFails()
        {
            // A timeout is not an error, any other read failure ends the loop
            // (backend:298-309).
            var io = new AnalogKeyboardTestTransport();
            var session = new RogAzoth96HeSession();
            session.Start(io);
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, Keys(), null));
            io.Gone = true;
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, Keys(), null));
        }

        [Fact]
        public void Azoth_Stop_WritesNothing()
        {
            // HallJoy's stop only cancels and closes (backend:339-360).
            var io = new AnalogKeyboardTestTransport();
            var session = new RogAzoth96HeSession();
            session.Start(io);
            int before = io.Log.Count;
            session.Stop(io);
            Assert.Equal(before, io.Log.Count);
        }

        [Fact]
        public void Azoth_Table_IsTheVendorRange()
        {
            // No key map exists (recon:92-101): 0x600 plus the firmware key.
            var table = RogAzoth96HeProtocol.Table();
            Assert.Equal(256, table.Length);
            Assert.Equal(0x600, table[0]);
            Assert.Equal(0x6FF, table[0xFF]);
            Assert.Equal(0, RogAzoth96HeProtocol.CodeFor(table, 0x100));
            Assert.Equal(256, new RogAzoth96HeSession().KeyOrder.Length);
        }

        // ── Logitech PRO X TKL RAPID ───────────────────────────────────────

        private static AnalogKeyboardDeviceInfo RapidInfo(ushort pid = 0xC35B, ushort usage = 0x0002, byte reportId = 0x11)
            => Info(0x046D, pid, 0xFF00, usage, 20, 20, new[] { reportId }, new[] { reportId });

        [Fact]
        public void Rapid_Matches_TheHidppLongCollection_AndRejectsNearMisses()
        {
            // logitech-analogue-report.cpp:3-6 (046D, FF00:2) and PID C35B
            // (OpenRGB RGBController_LogitechHIDPP20.cpp:382 and 461, Solaar
            // test_perkey_layouts.py:13).
            Assert.True(LogitechRapidProtocol.Matches(RapidInfo()));
            Assert.False(LogitechRapidProtocol.Matches(RapidInfo(usage: 0x0001, reportId: 0x10)));
            Assert.False(LogitechRapidProtocol.Matches(RapidInfo(pid: 0xC35C)));
            Assert.False(LogitechRapidProtocol.Matches(RapidInfo(pid: 0xC359)));   // G915 X Wired
            Assert.False(LogitechRapidProtocol.Matches(RapidInfo(reportId: 0x12)));
            var otherVendor = RapidInfo();
            otherVendor.VendorId = 0x046E;
            Assert.False(LogitechRapidProtocol.Matches(otherVendor));
        }

        [Fact]
        public void Rapid_Pass_ReadsTheGistsCapture()
        {
            // pressing-a.txt: byte 4 = 23, byte 5 rising to 28 (40) and back.
            // releasing-d.txt: byte 4 = 22 at depth 0.
            var io = new AnalogKeyboardTestTransport();
            var session = new LogitechRapidSession();
            Assert.True(session.Start(io));
            var output = Keys();

            io.QueueInput(Hex("11FF0F0023020100020000000000000000000000"));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(2 / 40f, output.Get(AnalogKeyCodes.A), 6);

            io.QueueInput(Hex("11FF0F0023280101280000000000000000000000"));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.A));

            io.QueueInput(Hex("11FF0F0023000000000000000000000000000000"));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0, output.Count);

            io.QueueInput(Hex("11FF0F0022140101140000000000000000000000"));
            session.Pass(io, output, null);
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.D));
            io.QueueInput(Hex("11FF0F0022000000000000000000000000000000"));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0, output.Count);
            Assert.Empty(io.Log);
        }

        [Fact]
        public void Rapid_Pass_KeepsOnlyTheReportedKey()
        {
            // The firmware reports only the deepest key (The List, issue #1).
            var io = new AnalogKeyboardTestTransport();
            var session = new LogitechRapidSession();
            var output = Keys();
            io.QueueInput(Hex("11FF0F0023140101140000000000000000000000"),
                Hex("11FF0F00221E01011E0000000000000000000000"));
            session.Pass(io, output, null);
            session.Pass(io, output, null);
            Assert.Equal(0f, output.Get(AnalogKeyCodes.A));
            Assert.Equal(0.75f, output.Get(AnalogKeyCodes.D));
            Assert.Equal(1, output.Count);
        }

        [Fact]
        public void Rapid_Pass_PublishesUnmappedKeysInTheVendorRange()
        {
            // Only 0x22 and 0x23 have a captured key (releasing-d.txt,
            // pressing-a.txt). Byte 4 = 05 at depth 0x10 is 0x605 at 16/40.
            var io = new AnalogKeyboardTestTransport();
            var session = new LogitechRapidSession();
            var output = Keys();
            io.QueueInput(Hex("11FF0F0005100101100000000000000000000000"));
            Assert.Equal(AnalogPollResult.Ok, session.Pass(io, output, null));
            Assert.Equal(0.4f, output.Get(0x605), 6);
        }

        [Fact]
        public void Rapid_Pass_SkipsOtherHidppTraffic()
        {
            // Only 11 FF 0F 00 carries travel (pressing-a.txt).
            var io = new AnalogKeyboardTestTransport();
            var session = new LogitechRapidSession();
            var output = Keys();
            output.Set(AnalogKeyCodes.W, 0.5f);
            foreach (var hex in new[]
            {
                "11FF0E0023140101140000000000000000000000",   // another feature
                "11010F0023140101140000000000000000000000",   // another device index
                "11FF0F1023140101140000000000000000000000",   // function 1, an answer
                "10FF0F0023140101",                           // a short report
                "11FF0F0023",                                 // no depth byte
            })
            {
                io.QueueInput(Hex(hex));
                Assert.Equal(AnalogPollResult.Idle, session.Pass(io, output, null));
            }
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));
        }

        [Fact]
        public void Rapid_Pass_SilenceIsIdle_GoneFails_StopWritesNothing()
        {
            // The gist only reads (logitech-analogue-report.cpp:8-11).
            var io = new AnalogKeyboardTestTransport();
            var session = new LogitechRapidSession();
            Assert.Equal(AnalogPollResult.Idle, session.Pass(io, Keys(), null));
            session.Stop(io);
            Assert.Empty(io.Log);
            io.Gone = true;
            Assert.Equal(AnalogPollResult.Failed, session.Pass(io, Keys(), null));
        }

        [Fact]
        public void Rapid_Table_NamesTheTwoCapturedKeys()
        {
            // pressing-a.txt (23 is A) and releasing-d.txt (22 is D), the
            // vendor range for the rest.
            var table = LogitechRapidProtocol.Table();
            Assert.Equal(256, table.Length);
            Assert.Equal(AnalogKeyCodes.A, table[0x23]);
            Assert.Equal(AnalogKeyCodes.D, table[0x22]);
            Assert.Equal(0x600, table[0x00]);
            Assert.Equal(0x621, table[0x21]);
            Assert.Equal(0x624, table[0x24]);
            Assert.Equal(0x6FF, table[0xFF]);
            var order = new LogitechRapidSession().KeyOrder;
            Assert.Equal(256, order.Length);
            Assert.Contains(AnalogKeyCodes.A, order);
            Assert.All(order, code => Assert.True(code < AnalogKeyInputState.CodeCount));
        }
    }
}
