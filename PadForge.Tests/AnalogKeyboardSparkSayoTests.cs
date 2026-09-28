using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Engine;
using PadForge.Engine.Common.AnalogKeyboard;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// HallJoy's SparkLink V2 and SayoDevice depth routes (issue #468). No
    /// SparkLink or Sayo keyboard is on the bench, so both are pinned against
    /// byte fixtures from HallJoy's source, its own unit tests and its
    /// self-test (AGPL-3.0, commit 378f9fe8): backend_sparklink.inc,
    /// sparklink_model_profiles.h, sparklink_key_codes.h,
    /// sparklink_row_freshness.h, sparklink_hotplug_age.h, backend_sayo.inc,
    /// sayo_o3c_protocol.h and backend.cpp. Buffers are what Windows hands
    /// over: byte 0 is the report ID, 0 for SparkLink's unnumbered reports and
    /// 0x22 for Sayo's.
    /// </summary>
    public class AnalogKeyboardSparkSayoTests
    {
        // ── Shared fixtures ───────────────────────────────────────────────

        /// <summary>A millisecond clock that moves one step per reading, so a
        /// wait on an empty fake transport ends instead of spinning.</summary>
        private sealed class TestClock
        {
            public long Now = 100_000;
            public long Step = 1;
            public long Read() => Now += Step;
        }

        /// <summary>A transport whose writes can fail by kind, for the write
        /// fallbacks and the read error paths.</summary>
        private sealed class SwitchTransport : IAnalogKeyboardTransport
        {
            private readonly Queue<byte[]> _input = new();
            public readonly List<(string Kind, byte[] Data)> Log = new();
            public Func<byte[], IEnumerable<byte[]>> Respond = _ => Array.Empty<byte[]>();
            public bool SendWorks = true;
            public bool OutputReportWorks = true;
            public bool FeatureWorks = true;
            public bool ReadFails;
            public int InputLength { get; set; } = 65;
            public int OutputLength { get; set; } = 65;
            public int FeatureLength { get; set; } = 65;

            private bool Write(string kind, byte[] report, bool works)
            {
                Log.Add((kind, (byte[])report.Clone()));
                if (works) foreach (var r in Respond(report)) _input.Enqueue(r);
                return works;
            }

            public void QueueInput(byte[] report) => _input.Enqueue(report);

            public bool Send(byte[] report) => Write("out", report, SendWorks);
            public bool SendOutputReport(byte[] report) => Write("ctl", report, OutputReportWorks);
            public bool SetFeature(byte[] report) => Write("setf", report, FeatureWorks);
            public int GetFeature(byte[] buffer) => -1;
            public void DiscardStale() { }

            public int Receive(byte[] buffer, int timeoutMs)
            {
                if (ReadFails) return -1;
                if (_input.Count == 0) return 0;
                var r = _input.Dequeue();
                Array.Copy(r, buffer, r.Length);
                return r.Length;
            }
        }

        // ── SparkLink fixtures ────────────────────────────────────────────

        /// <summary>The IROK MG75 Max firmware 1.1.3.0 factory base layer, 6
        /// rows of 21 vendor codes, Fn 0xF101 at row 5 column 11. HallJoy's
        /// evidence file, docs/research/MG75_FN_EVIDENCE_2026-09-19.json:8-135.</summary>
        private static readonly ushort[][] Mg75MaxFactory =
        {
            new ushort[] { 0x29, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x4C, 0, 0, 0, 0, 0, 0 },
            new ushort[] { 0x35, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x2D, 0x2E, 0x2A, 0x49, 0, 0, 0, 0, 0, 0 },
            new ushort[] { 0x2B, 0x14, 0x1A, 0x08, 0x15, 0x17, 0x1C, 0x18, 0x0C, 0x12, 0x13, 0x2F, 0x30, 0x31, 0x4B, 0, 0, 0, 0, 0, 0 },
            new ushort[] { 0x39, 0x04, 0x16, 0x07, 0x09, 0x0A, 0x0B, 0x0D, 0x0E, 0x0F, 0x33, 0x34, 0x00, 0x28, 0x4E, 0, 0, 0, 0, 0, 0 },
            new ushort[] { 0xE1, 0x00, 0x1D, 0x1B, 0x06, 0x19, 0x05, 0x11, 0x10, 0x36, 0x37, 0x38, 0xE5, 0x52, 0x00, 0, 0, 0, 0, 0, 0 },
            new ushort[] { 0xE0, 0xE3, 0xE2, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x00, 0x00, 0xE6, 0xF101, 0x50, 0x51, 0x4F, 0, 0, 0, 0, 0, 0 },
        };

        /// <summary>A 21-column row with the given (column, value) cells.</summary>
        private static ushort[] Row(params (int col, int value)[] cells)
        {
            var row = new ushort[SparkLinkProtocol.ColumnsPerRow];
            foreach (var (col, value) in cells) row[col] = (ushort)value;
            return row;
        }

        /// <summary>A SparkLink report as Windows writes and reads it on a
        /// 65-byte collection: 00, then the 64-byte packet.</summary>
        private static byte[] Wire(params byte[] head)
        {
            var r = new byte[SparkLinkProtocol.PayloadSize + 1];
            Array.Copy(head, 0, r, 1, head.Length);
            return r;
        }

        /// <summary>An answer: the echoed header, then 21 little-endian values
        /// from byte 4 (backend_sparklink.inc:765-769, :782-786).</summary>
        private static byte[] SparkAnswer(byte[] header, ushort[] values, bool reportId = true)
        {
            var payload = new byte[SparkLinkProtocol.PayloadSize];
            header.CopyTo(payload, 0);
            if (values != null)
                for (int c = 0; c < values.Length; c++)
                {
                    payload[4 + 2 * c] = (byte)values[c];
                    payload[5 + 2 * c] = (byte)(values[c] >> 8);
                }
            return reportId ? Wire(payload) : payload;
        }

        /// <summary>A device-info answer: echo 01 02, type, subtype 16, board
        /// 0x06050529, app 1.1.3.0, pcb 1-2-3-4, run mode 2.</summary>
        private static byte[] DeviceInfoAnswer(byte type) => Wire(
            0x01, 0x02, type, 16, 0x06, 0x05, 0x05, 0x29, 1, 1, 3, 0, 1, 2, 3, 4, 2);

        /// <summary>A scripted SparkLink keyboard: answers device info, the
        /// layout rows of <see cref="Layout"/> (rows past it answer empty, a
        /// null row does not answer) and the route rows <see cref="Route"/>
        /// returns (null does not answer).</summary>
        private sealed class SparkKeyboard
        {
            public byte Type = SparkLinkProtocol.KeyboardType;
            public int DeviceInfoAnswers = int.MaxValue;
            public ushort[][] Layout;
            public Func<int, ushort[]> Route = _ => new ushort[SparkLinkProtocol.ColumnsPerRow];
            public bool ReportId = true;
            public readonly AnalogKeyboardTestTransport Io = new() { FeatureLength = 0 };

            public SparkKeyboard(ushort[][] layout)
            {
                Layout = layout;
                Io.OnSend = Respond;
            }

            public IEnumerable<byte[]> Respond(byte[] report)
            {
                var p = report.Length > SparkLinkProtocol.PayloadSize ? report.Skip(1).ToArray() : report;
                var answers = new List<byte[]>();
                if (p[0] == 0x01 && p[1] == 0x02)
                {
                    if (DeviceInfoAnswers-- > 0) answers.Add(DeviceInfoAnswer(Type));
                }
                else if (p[0] == 0x03 && p[1] == 0x01 && p[2] == 0x00)
                {
                    int row = p[3];
                    var codes = row < Layout.Length ? Layout[row] : new ushort[SparkLinkProtocol.ColumnsPerRow];
                    if (codes != null) answers.Add(SparkAnswer(new byte[] { 0x03, 0x01, 0x00, (byte)row }, codes, ReportId));
                }
                else if (p[0] == 0x04 && p[1] == 0x03 && p[2] == 0x01)
                {
                    var travel = Route(p[3]);
                    if (travel != null) answers.Add(SparkAnswer(new byte[] { 0x04, 0x03, 0x01, p[3] }, travel, ReportId));
                }
                return answers;
            }
        }

        private static AnalogKeyboardDeviceInfo SparkInfo(ushort pid = SparkLinkProtocol.ConfirmedProductId,
            ushort page = SparkLinkProtocol.UsagePage, ushort usage = SparkLinkProtocol.Usage,
            ushort inLen = 65, ushort outLen = 65, ushort vid = SparkLinkProtocol.VendorId,
            string path = null, string product = "keyboard") => new()
        {
            VendorId = vid,
            ProductId = pid,
            UsagePage = page,
            Usage = usage,
            InputReportLength = inLen,
            OutputReportLength = outLen,
            ProductString = product,
            Path = path ?? $@"\\?\hid#vid_{vid:x4}&pid_{pid:x4}&mi_01#7&2b4c6e8a&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
        };

        private static SparkLinkSession StartedSpark(SparkKeyboard kb, TestClock clock, ushort pid = 0x0529)
        {
            var s = new SparkLinkSession(pid, clock.Read);
            Assert.True(s.Start(kb.Io));
            return s;
        }

        // ── SparkLink identity ────────────────────────────────────────────

        [Fact]
        public void SparkLink_Matches_EveryAdmittedIdentity()
        {
            // sparklink_model_profiles.h:8-43: the MG75 Max plus the 28
            // experimental PIDs, 8 IROK and CAROTMAS and 21 EWEADN, all VID 1CA6.
            ushort[] expected =
            {
                0x0529, 0x0528, 0x052A, 0x052B, 0x052D, 0x052C, 0x0540, 0x0531,
                0x1C0A, 0x1C0C, 0x1C12, 0x1C14, 0x1C1A, 0x1C1F, 0x1C23, 0x1C24, 0x1C2B, 0x1C2C,
                0x1C2F, 0x1C37, 0x1C3C, 0x1C3D, 0x1C45, 0x1C4A, 0x1C4C, 0x2708, 0x2709, 0x270A, 0x5E01,
            };
            Assert.Equal(expected, SparkLinkProtocol.AdmittedProductIds);
            Assert.Equal(8, expected.Count(p => (p >> 8) == 0x05));
            Assert.Equal(21, expected.Count(p => (p >> 8) != 0x05));
            var route = SparkSayoRoutes.All[0];
            foreach (ushort pid in expected)
            {
                Assert.True(route.Matches(SparkInfo(pid)), $"{pid:X4}");
                Assert.False(string.IsNullOrEmpty(SparkLinkProtocol.ModelName(pid)));
            }
            // Reports of exactly 64 bytes are enough (backend_sparklink.inc:1068).
            Assert.True(route.Matches(SparkInfo(inLen: 64, outLen: 64)));
        }

        [Fact]
        public void SparkLink_Matches_RejectsNearMisses()
        {
            var route = SparkSayoRoutes.All[0];
            // sparklink_model_profiles_test.cpp:12-14: wrong page, wrong usage,
            // an unlisted PID.
            Assert.False(route.Matches(SparkInfo(page: 0xFFC0)));
            Assert.False(route.Matches(SparkInfo(usage: 2)));
            Assert.False(route.Matches(SparkInfo(pid: 0xFFFF)));
            // The legacy FFA0 row protocol is never admitted (backend_sparklink.inc:13, :446-449).
            Assert.False(route.Matches(SparkInfo(page: 0xFFA0)));
            // Another vendor ID with an admitted PID (sparklink_model_profiles.h:42-44).
            Assert.False(route.Matches(SparkInfo(vid: 0x1CA5)));
            // EWEADN identities HallJoy leaves out: Alpha 87 (1C3E), the X75 V3
            // and Gamma75 revision on 1C2D, and the shared ES68 boot identity
            // (docs/research/eweadn-sparklink/catalog.json:398-403).
            Assert.False(route.Matches(SparkInfo(pid: 0x1C3E)));
            Assert.False(route.Matches(SparkInfo(pid: 0x1C2D)));
            Assert.False(route.Matches(SparkInfo(vid: 0x1A86, pid: 0xFE81)));
            // Reports shorter than a packet (backend_sparklink.inc:1068).
            Assert.False(route.Matches(SparkInfo(inLen: 63)));
            Assert.False(route.Matches(SparkInfo(outLen: 63)));
            Assert.False(route.Matches(null));
        }

        [Fact]
        public void SparkLink_NeverMatchesASteelSeriesApexPro()
        {
            // Device info 01 02 resets an Apex Pro (backend_sparklink.inc:1016-1017),
            // so its identity never passes, whatever page it shows
            // (sparklink_model_profiles_test.cpp:9-11).
            Assert.False(SparkLinkProtocol.ProbeIdentity(0x1038, 0x1610));
            Assert.False(SparkLinkProtocol.ProbeInterface(0x1038, 0x1610, 0xFFC0, 1));
            Assert.False(SparkLinkProtocol.ProbeInterface(0x1038, 0x1610, 0xFFB0, 1));
            var route = SparkSayoRoutes.All[0];
            Assert.False(route.Matches(SparkInfo(vid: 0x1038, pid: 0x1610, page: 0xFFC0)));
            Assert.False(route.Matches(SparkInfo(vid: 0x1038, pid: 0x1610)));
            Assert.True(SparkLinkProtocol.ProbeInterface(0x1CA6, 0x0529, 0xFFB0, 1));
            Assert.False(SparkLinkProtocol.ProbeInterface(0x1CA6, 0x0529, 0xFFC0, 1));
            Assert.False(SparkLinkProtocol.ProbeInterface(0x1CA6, 0x0529, 0xFFB0, 2));
        }

        [Fact]
        public void SparkLink_SkipsTheSparkPlayJoy6x21PathsBeforeAnything()
        {
            // backend_sparklink.inc:1010-1014 with aula_win60he_protocol.h:29-36:
            // the six 6x21 identities, read from the path text in any case.
            var route = SparkSayoRoutes.All[0];
            (ushort vid, ushort pid)[] dedicated =
            {
                (0x1CA2, 0x1902), (0x1CA2, 0x1901), (0x1CA5, 0x0409),
                (0x1CA5, 0x2201), (0x1CA5, 0x2202), (0x1CA2, 0x2201),
            };
            foreach (var (vid, pid) in dedicated)
            {
                string upper = $@"\\?\HID#VID_{vid:X4}&PID_{pid:X4}&MI_01#7&1&0&0000#{{4D1E55B2-F16F-11CF-88CB-001111000030}}";
                Assert.True(SparkLinkProtocol.PathIsDedicated6x21(upper));
                Assert.True(SparkLinkProtocol.PathIsDedicated6x21(upper.ToLowerInvariant()));
                // The path decides, before the attributes are read.
                Assert.False(route.Matches(SparkInfo(path: upper)));
            }
            Assert.False(SparkLinkProtocol.PathIsDedicated6x21(SparkInfo().Path));

            // The first well-formed token wins (aula_win60he_protocol.h:125-140).
            Assert.True(SparkLinkProtocol.TryReadUsbIdentityFromPath(@"x#vid_1cg2&pid_0001#vid_1CA6&pid_052b#", out var v, out var p));
            Assert.Equal(0x1CA6, v);
            Assert.Equal(0x052B, p);
            Assert.False(SparkLinkProtocol.TryReadUsbIdentityFromPath("vid_1ca6&pid_05", out _, out _));
            Assert.False(SparkLinkProtocol.TryReadUsbIdentityFromPath(null, out _, out _));
        }

        [Fact]
        public void SparkLink_ProductStringIsNotChecked_AndNamesFollowHallJoy()
        {
            // No string check anywhere in the admission (backend_sparklink.inc:946-1176).
            var route = SparkSayoRoutes.All[0];
            Assert.True(route.Matches(SparkInfo(pid: 0x052B, product: "CAROTMAS Mars75")));
            Assert.True(route.Matches(SparkInfo(pid: 0x052B, product: "")));
            // Names from HallJoy's catalogs (keyboard_support_notices.json,
            // SUPPORTED_HARDWARE.md). Shared PIDs carry both models.
            Assert.Equal("IROK MG75 Max", route.Name(SparkInfo()));
            Assert.Equal("IROK Mars75 / Mars75 Pro", SparkLinkProtocol.ModelName(0x052B));
            Assert.Equal("EWEADN DK68 Star HE / DK63 Star HE", SparkLinkProtocol.ModelName(0x1C2B));
            Assert.Equal("IROK Mercury68 SE (JingTai V2)", SparkLinkProtocol.ModelName(0x0540));
            Assert.Equal("EWEADN DK63 HE", SparkLinkProtocol.ModelName(0x5E01));
            Assert.Null(SparkLinkProtocol.ModelName(0x1C3E));
        }

        [Fact]
        public void Routes_AreHallJoysLastTwo_SparkLinkFirst()
        {
            // native_analog_backends.def:49-50, and the handles each opens:
            // read/write, shared, 64 input buffers (backend_sparklink.inc:1027-1049,
            // :1086, backend_sayo.inc:532-554, :571).
            var all = SparkSayoRoutes.All;
            Assert.Equal(2, all.Count);
            Assert.Equal(AnalogKeyboardProtocol.SparkLink, all[0].Protocol);
            Assert.Equal(AnalogKeyboardProtocol.Sayo, all[1].Protocol);
            Assert.Equal("halljoy-sparklink", all[0].Id);
            Assert.Equal("halljoy-sayo-depth", all[1].Id);
            foreach (var route in all)
            {
                Assert.True(route.Writable);
                Assert.False(route.Exclusive);
                Assert.Equal(64, route.InputBuffers);
                Assert.Null(route.Companion);
            }
            Assert.IsType<SparkLinkSession>(all[0].CreateSession(SparkInfo()));
            Assert.IsType<SayoDepthSession>(all[1].CreateSession(SayoInfo()));
        }

        // ── SparkLink wire ────────────────────────────────────────────────

        [Fact]
        public void SparkLink_Requests_ByteForByte()
        {
            // SparkFillPacket and the three builders (backend_sparklink.inc:737-788).
            var info = SparkLinkProtocol.DeviceInfoRequest();
            Assert.Equal(64, info.Length);
            Assert.Equal(new byte[] { 0x01, 0x02 }, info.Take(2).ToArray());
            Assert.All(info.Skip(2), b => Assert.Equal(0, b));
            var layout = SparkLinkProtocol.LayoutRowRequest(5);
            Assert.Equal(new byte[] { 0x03, 0x01, 0x00, 0x05 }, layout.Take(4).ToArray());
            Assert.All(layout.Skip(4), b => Assert.Equal(0, b));
            var route = SparkLinkProtocol.RouteRowRequest(5);
            Assert.Equal(new byte[] { 0x04, 0x03, 0x01, 0x05 }, route.Take(4).ToArray());
            Assert.All(route.Skip(4), b => Assert.Equal(0, b));
        }

        [Fact]
        public void SparkLink_DecodeKey_FnAndPlainUsages()
        {
            // sparklink_model_profiles_test.cpp:27-28 and sparklink_key_codes.h:8-10.
            Assert.Equal(0x409, SparkLinkProtocol.DecodeKey(0xF101));
            Assert.Equal(AnalogKeyCodes.Fn, SparkLinkProtocol.DecodeKey(0xF101));
            Assert.Equal(0, SparkLinkProtocol.DecodeKey(0xF102));
            for (int key = 4; key < 256; key++) Assert.Equal(key, SparkLinkProtocol.DecodeKey(key));
            // Code 1 is not Fn here, and passes through as HallJoy publishes it
            // (docs/current/MG75_FN_REVIEW_2026-09-19.md:17).
            Assert.Equal(1, SparkLinkProtocol.DecodeKey(1));
            Assert.Equal(0, SparkLinkProtocol.DecodeKey(0));
            Assert.Equal(0, SparkLinkProtocol.DecodeKey(0x0100));
        }

        [Fact]
        public void SparkLink_LayoutAnswer_DecodesTheMg75MaxBottomRow()
        {
            // The spec's Appendix B layout answer for row 5, bytes 0 to 45
            // (from MG75_FN_EVIDENCE_2026-09-19.json:114-134), after the 00
            // report ID, read at offset 4 + 2c (backend_sparklink.inc:765-769).
            byte[] head =
            {
                0x03, 0x01, 0x00, 0x05, 0xE0, 0x00, 0xE3, 0x00, 0xE2, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x2C, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xE6, 0x00, 0x01, 0xF1, 0x50, 0x00, 0x51, 0x00,
                0x4F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            };
            var report = Wire(head);
            Assert.Equal(1, SparkLinkProtocol.FindPacket(report, 0x03, 0x01, 0x00, 0x05));
            Assert.Equal(-1, SparkLinkProtocol.FindPacket(report, 0x03, 0x01, 0x00, 0x04));
            var codes = new ushort[21];
            SparkLinkProtocol.ReadRow(report.AsSpan(1), codes);
            var keys = codes.Select(c => SparkLinkProtocol.DecodeKey(c)).ToArray();
            Assert.Equal(new[] { 224, 227, 226, 0, 0, 0, 44, 0, 0, 0, 230, 1033, 80, 81, 79, 0, 0, 0, 0, 0, 0 }, keys);
            Assert.Equal(AnalogKeyCodes.LCtrl, keys[0]);
            Assert.Equal(AnalogKeyCodes.Space, keys[6]);
            Assert.Equal(AnalogKeyCodes.Fn, keys[11]);
            Assert.Equal(AnalogKeyCodes.ArrowRight, keys[14]);
        }

        [Fact]
        public void SparkLink_FindPacket_OffsetZeroOrOne_WholePacketsOnly()
        {
            // SparkFindPacket (backend_sparklink.inc:610-628): offset 0 first,
            // then 1, and 64 bytes must remain at the offset.
            var bare = SparkAnswer(new byte[] { 0x04, 0x03, 0x01, 0x02 }, null, reportId: false);
            Assert.Equal(64, bare.Length);
            Assert.Equal(0, SparkLinkProtocol.FindPacket(bare, 0x04, 0x03, 0x01, 0x02));
            Assert.Equal(-1, SparkLinkProtocol.FindPacket(bare.AsSpan(0, 63), 0x04, 0x03, 0x01, 0x02));
            var numbered = Wire(0x04, 0x03, 0x01, 0x02);
            Assert.Equal(1, SparkLinkProtocol.FindPacket(numbered, 0x04, 0x03, 0x01, 0x02));
            // 64 bytes with the report ID in front leave 63 at offset 1.
            Assert.Equal(-1, SparkLinkProtocol.FindPacket(numbered.AsSpan(0, 64), 0x04, 0x03, 0x01, 0x02));
            // Device info matches on its two echo bytes alone.
            Assert.Equal(1, SparkLinkProtocol.FindPacket(DeviceInfoAnswer(1), 0x01, 0x02));
        }

        [Fact]
        public void SparkLink_DeviceInfoFields_AsHallJoyLogsThem()
        {
            // SparkLogDeviceInfo (backend_sparklink.inc:351-368).
            var fields = SparkLinkProtocol.ParseDeviceInfo(DeviceInfoAnswer(1).AsSpan(1));
            Assert.Equal(1, fields.Type);
            Assert.Equal(16, fields.Subtype);
            Assert.Equal(0x06050529u, fields.BoardId);
            Assert.Equal("1.1.3.0", fields.App);
            Assert.Equal("1-2-3-4", fields.Pcb);
            Assert.Equal(2, fields.RunMode);
        }

        // ── SparkLink normalization and aggregation ───────────────────────

        [Fact]
        public void SparkLink_Normalize_StartsAt3500_FollowsTheLargestUpTo5000()
        {
            // SparkNormalizeRouteToMilli (backend_sparklink.inc:877-892), with
            // HallJoy's own check values 1750 to 500 and 3500 to 1000
            // (backend.cpp:4650-4654).
            var m = new SparkLinkMatrix();
            int w = AnalogKeyCodes.W, a = AnalogKeyCodes.A, s = AnalogKeyCodes.S;
            Assert.Equal(500, m.NormalizeRouteToMilli(w, 1750));
            Assert.Equal(1000, m.NormalizeRouteToMilli(w, 3500));
            Assert.Equal(0, m.NormalizeRouteToMilli(w, 0));
            Assert.Equal(0, m.NormalizeRouteToMilli(0, 3500));
            // No rest threshold: raw 1 is 0 and raw 2 is 1 at 3500.
            Assert.Equal(0, m.NormalizeRouteToMilli(w, 1));
            Assert.Equal(1, m.NormalizeRouteToMilli(w, 2));
            // The denominator rises with the key and never falls back.
            Assert.Equal(1000, m.NormalizeRouteToMilli(a, 4000));
            Assert.Equal(500, m.NormalizeRouteToMilli(a, 2000));
            // Past 5000 the value saturates and the denominator stays.
            Assert.Equal(1000, m.NormalizeRouteToMilli(s, 6000));
            Assert.Equal(500, m.NormalizeRouteToMilli(s, 1750));
            Assert.Equal(1000, m.NormalizeRouteToMilli(s, 5000));
            Assert.Equal(0, m.NormalizeRouteToMilli(s, 2));
            Assert.Equal(1, m.NormalizeRouteToMilli(s, 3));
            // Each key keeps its own denominator: W is still at 3500.
            Assert.Equal(500, m.NormalizeRouteToMilli(w, 1750));
            // A new session starts every key at 3500 again (:99-135).
            m.Reset();
            Assert.Equal(500, m.NormalizeRouteToMilli(a, 1750));
        }

        [Fact]
        public void SparkLink_FnOnTwoRows_MatchesHallJoysSelfTest()
        {
            // Backend_TestSparkFnPublication (backend.cpp:4638-4660), step for
            // step: Fn mapped at row 5 column 11 and row 4 column 1.
            var m = new SparkLinkMatrix();
            m.SetLayoutRow(5, Row((11, 0xF101)));
            m.SetLayoutRow(4, Row((1, 0xF101)));
            m.SetRowCount(6);
            int fn = AnalogKeyCodes.Fn;
            long now = 500_000;

            m.RecordRouteResult(5, true, now);
            m.CommitRouteRow(5, Row((11, 1750)), now, 6);
            Assert.True(m.OwnsHid(fn));
            Assert.Equal(500, m.GetMilli(fn));

            m.RecordRouteResult(4, true, now);
            m.CommitRouteRow(4, Row((1, 3500)), now, 6);
            m.RecordRouteResult(5, true, now);
            m.CommitRouteRow(5, Row(), now, 6);
            Assert.Equal(1000, m.GetMilli(fn));   // row 4 still holds it

            m.RecordRouteResult(4, true, now);
            m.CommitRouteRow(4, Row(), now, 6);
            Assert.Equal(0, m.GetMilli(fn));

            m.RecordRouteResult(5, true, now);
            m.CommitRouteRow(5, Row((11, 3500)), now, 6);
            Assert.Equal(1000, m.GetMilli(fn));
            Assert.True(m.ReconcileRowFreshness(now + SparkLinkProtocol.RowFreshnessMs + 1, 6));
            Assert.Equal(0, m.GetMilli(fn));
            Assert.Equal(0, m.GetMilli(1));
        }

        [Fact]
        public void SparkLink_RowFreshnessAndSessionAge_HallJoysVectors()
        {
            // sparklink_row_freshness_test.cpp:26-28, with the 2160 ms deadline
            // of backend_sparklink.inc:21-24.
            Assert.Equal(2160, SparkLinkProtocol.RowFreshnessMs);
            Assert.False(SparkLinkProtocol.IsRowFresh(100, 0, 2160));
            Assert.True(SparkLinkProtocol.IsRowFresh(2260, 100, 2160));
            Assert.False(SparkLinkProtocol.IsRowFresh(2261, 100, 2160));
            // sparklink_hotplug_age_test.cpp:10-15, :24.
            Assert.Equal(0, SparkLinkProtocol.FreshnessAgeMs(100, 0));
            Assert.Equal(0, SparkLinkProtocol.FreshnessAgeMs(100, 101));
            Assert.Equal(1, SparkLinkProtocol.FreshnessAgeMs(101, 100));
            Assert.False(SparkLinkProtocol.IsPacketStale(100, 101, 1800));
            Assert.False(SparkLinkProtocol.IsPacketStale(1900, 100, 1800));
            Assert.True(SparkLinkProtocol.IsPacketStale(1901, 100, 1800));
            Assert.True(SparkLinkProtocol.IsPacketStale(2001, 100, 1800));
        }

        [Fact]
        public void SparkLink_ExpiredRowDropsOnlyItsKeys()
        {
            // sparklink_row_freshness_test.cpp:30-33: row A stays live while
            // row B's older value expires. Here W on row 0 and on row 1.
            var m = new SparkLinkMatrix();
            m.SetLayoutRow(0, Row((0, AnalogKeyCodes.W)));
            m.SetLayoutRow(1, Row((0, AnalogKeyCodes.W), (1, AnalogKeyCodes.A)));
            m.SetRowCount(2);
            m.RecordRouteResult(1, true, 2900);
            m.CommitRouteRow(1, Row((0, 3150), (1, 3500)), 2900, 2);   // W 900, A 1000
            m.RecordRouteResult(0, true, 3000);
            m.CommitRouteRow(0, Row((0, 2450)), 3000, 2);              // W 700
            Assert.Equal(900, m.GetMilli(AnalogKeyCodes.W));
            Assert.False(m.ReconcileRowFreshness(3000 + 2000, 2));
            m.RecordRouteResult(0, true, 5200);
            m.CommitRouteRow(0, Row((0, 2450)), 5200, 2);
            Assert.True(m.ReconcileRowFreshness(5200, 2));
            Assert.Equal(700, m.GetMilli(AnalogKeyCodes.W));
            Assert.Equal(0, m.GetMilli(AnalogKeyCodes.A));
            // A row past the row count stops counting at once (:280-298).
            m.RecordRouteResult(1, true, 5300);
            m.CommitRouteRow(1, Row((0, 3500)), 5300, 2);
            Assert.Equal(1000, m.GetMilli(AnalogKeyCodes.W));
            Assert.True(m.ReconcileRowFreshness(5300, 1));
            Assert.Equal(700, m.GetMilli(AnalogKeyCodes.W));
        }

        // ── SparkLink Start ───────────────────────────────────────────────

        [Fact]
        public void SparkLink_Start_ProvesTheProtocol_ThenReadsTheBaseLayer()
        {
            // SparkStart's proof (backend_sparklink.inc:1688-1701), the
            // worker's second device info (:1204-1211) and layout discovery
            // (:894-944): rows 0 to 5 hold keys, rows 6 and 7 are empty, and
            // the second empty row ends the reads.
            var kb = new SparkKeyboard(Mg75MaxFactory);
            var s = StartedSpark(kb, new TestClock());
            var writes = kb.Io.Writes("out");
            Assert.Equal(10, writes.Count);
            Assert.All(writes, w => Assert.Equal(65, w.Length));
            Assert.Equal(Wire(0x01, 0x02), writes[0]);
            Assert.Equal(Wire(0x01, 0x02), writes[1]);
            for (int row = 0; row < 8; row++)
                Assert.Equal(Wire(0x03, 0x01, 0x00, (byte)row), writes[2 + row]);
            Assert.Empty(kb.Io.Writes("ctl"));
            Assert.Empty(kb.Io.Writes("setf"));
            Assert.Equal(0, kb.Io.Discards);   // HallJoy flushes nothing (:682-735)

            Assert.Equal(6, s.Matrix.RowCount);
            Assert.Equal("IROK MG75 Max", s.ModelName);
            Assert.Equal(16, s.DeviceInfo.Subtype);
            Assert.Equal(81, s.KeyOrder.Length);
            Assert.Equal(AnalogKeyCodes.Escape, s.KeyOrder[0]);
            Assert.Equal(AnalogKeyCodes.ArrowRight, s.KeyOrder[^1]);
            Assert.Contains(AnalogKeyCodes.Fn, s.KeyOrder);
            Assert.Equal(AnalogKeyCodes.W, s.Matrix.HidAt(2, 2));
            Assert.Equal(AnalogKeyCodes.Fn, s.Matrix.HidAt(5, 11));
            Assert.Equal(s.KeyOrder, AnalogKeyboardData.KeysOf(s.Matrix.Table));
        }

        [Fact]
        public void SparkLink_Start_RejectsAnotherDeviceType()
        {
            // Byte 2 must be 1, a keyboard (backend_sparklink.inc:1692).
            var kb = new SparkKeyboard(Mg75MaxFactory) { Type = 0x02 };
            var s = new SparkLinkSession(0x0529, new TestClock().Read);
            Assert.False(s.Start(kb.Io));
            Assert.Single(kb.Io.Writes("out"));
            Assert.Null(s.ModelName);
            Assert.Null(s.KeyOrder);
        }

        [Fact]
        public void SparkLink_Start_FailsWithoutAnAnswer_AndSkipsOtherReports()
        {
            // SparkTransact (backend_sparklink.inc:682-735): an answer whose
            // header does not match is dropped, and 250 ms without a match fails.
            var clock = new TestClock();
            var kb = new SparkKeyboard(Mg75MaxFactory) { DeviceInfoAnswers = 0 };
            kb.Io.QueueInput(Wire(0x01, 0x03, 0x01), Wire(0x04, 0x03, 0x01, 0x00), new byte[40]);
            long before = clock.Now;
            Assert.False(new SparkLinkSession(0x0529, clock.Read).Start(kb.Io));
            Assert.Single(kb.Io.Writes("out"));
            Assert.Equal(0, kb.Io.PendingInput);
            Assert.InRange(clock.Now - before, SparkLinkProtocol.TransactionTimeoutMs, SparkLinkProtocol.TransactionTimeoutMs + 10);
        }

        [Fact]
        public void SparkLink_Start_TheWorkersDeviceInfoMustAnswerToo()
        {
            // The worker quits when its own device info fails (:1204-1210).
            var kb = new SparkKeyboard(Mg75MaxFactory) { DeviceInfoAnswers = 1 };
            Assert.False(new SparkLinkSession(0x0529, new TestClock().Read).Start(kb.Io));
            Assert.Equal(2, kb.Io.Writes("out").Count);
        }

        [Fact]
        public void SparkLink_Start_NeedsALayoutRowWithAKey()
        {
            // No row with a code: all eight rows are asked and none counts
            // (backend_sparklink.inc:926-940, :1213-1220).
            var empty = new SparkKeyboard(new ushort[0][]);
            Assert.False(new SparkLinkSession(0x0529, new TestClock().Read).Start(empty.Io));
            Assert.Equal(2 + 8, empty.Io.Writes("out").Count);

            // A row of vendor actions only (0xF102) counts toward the rows but
            // maps no key, so nothing could be polled (:911-925). The start
            // fails rather than run a worker that quits after six passes.
            var vendorOnly = new SparkKeyboard(new[] { Row((0, 0xF102)) });
            var s = new SparkLinkSession(0x0529, new TestClock().Read);
            Assert.False(s.Start(vendorOnly.Io));
            Assert.Equal(1, s.Matrix.RowCount);
            Assert.False(s.Matrix.IsRowActive(0));
        }

        [Fact]
        public void SparkLink_Discovery_KeepsTheRowsBeforeAFailedQuery()
        {
            // A layout query that fails ends discovery with the rows found
            // (backend_sparklink.inc:907-909).
            var layout = new[] { Mg75MaxFactory[0], Mg75MaxFactory[1], Mg75MaxFactory[2], null };
            var kb = new SparkKeyboard(layout);
            var s = StartedSpark(kb, new TestClock());
            Assert.Equal(3, s.Matrix.RowCount);
            Assert.Equal(2 + 4, kb.Io.Writes("out").Count);
            Assert.DoesNotContain(AnalogKeyCodes.A, s.KeyOrder);
        }

        [Fact]
        public void SparkLink_Discovery_OneEmptyRowDoesNotEndIt()
        {
            // An empty run of one continues, two end it, and the count is the
            // highest row with a code plus one (backend_sparklink.inc:926-940).
            var layout = new[]
            {
                Row((0, AnalogKeyCodes.W)), Row(), Row((0, 0xF102)), Row((3, AnalogKeyCodes.A)), Row(), Row(), Row((0, AnalogKeyCodes.S)),
            };
            var kb = new SparkKeyboard(layout);
            var s = StartedSpark(kb, new TestClock());
            Assert.Equal(4, s.Matrix.RowCount);
            Assert.Equal(2 + 6, kb.Io.Writes("out").Count);
            Assert.True(s.Matrix.IsRowActive(0));
            Assert.False(s.Matrix.IsRowActive(1));
            Assert.False(s.Matrix.IsRowActive(2));   // vendor action only
            Assert.True(s.Matrix.IsRowActive(3));
            Assert.Equal(new[] { AnalogKeyCodes.W, AnalogKeyCodes.A }, s.KeyOrder);
        }

        // ── SparkLink Pass ────────────────────────────────────────────────

        [Fact]
        public void SparkLink_Pass_AsksOneActiveRowInTurn_AndKeepsTheOthers()
        {
            // One active row per pass, round robin (backend_sparklink.inc:1283-1317).
            // W (row 2) at 1750 and Fn (row 5) at 3500.
            var kb = new SparkKeyboard(Mg75MaxFactory)
            {
                Route = row => row == 2 ? Row((2, 1750)) : row == 5 ? Row((11, 3500)) : Row(),
            };
            var s = StartedSpark(kb, new TestClock());
            int before = kb.Io.Writes("out").Count;
            var output = new AnalogKeyInputState();
            for (int pass = 0; pass < 7; pass++)
                Assert.Equal(AnalogPollResult.Ok, s.Pass(kb.Io, output, null));
            var asked = kb.Io.Writes("out").Skip(before).ToList();
            Assert.Equal(7, asked.Count);
            int[] rows = { 0, 1, 2, 3, 4, 5, 0 };
            for (int i = 0; i < rows.Length; i++) Assert.Equal(Wire(0x04, 0x03, 0x01, (byte)rows[i]), asked[i]);
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.Fn));
            Assert.Equal(2, output.Count);
        }

        [Fact]
        public void SparkLink_Pass_SkipsInactiveRows()
        {
            // Rows without a key are never asked (backend_sparklink.inc:1290-1291).
            var layout = new[] { Row((0, AnalogKeyCodes.W)), Row(), Row((0, AnalogKeyCodes.A)) };
            var kb = new SparkKeyboard(layout);
            var s = StartedSpark(kb, new TestClock());
            int before = kb.Io.Writes("out").Count;
            var output = new AnalogKeyInputState();
            for (int pass = 0; pass < 3; pass++) s.Pass(kb.Io, output, null);
            var asked = kb.Io.Writes("out").Skip(before).Select(w => (int)w[4]).ToArray();
            Assert.Equal(new[] { 0, 2, 0 }, asked);
        }

        [Fact]
        public void SparkLink_Pass_TakesTheAnswerAtEitherOffset_AndDropsStrays()
        {
            // An answer without the 00 prefix matches at offset 0, and a stray
            // answer for another row or a short report is dropped
            // (backend_sparklink.inc:610-628, :727-729).
            var layout = new[] { Row((2, AnalogKeyCodes.W)) };
            var kb = new SparkKeyboard(layout) { ReportId = false, Route = _ => Row((2, 3500)) };
            var s = StartedSpark(kb, new TestClock());
            kb.Io.QueueInput(Wire(0x04, 0x03, 0x01, 0x03), new byte[20]);
            var output = new AnalogKeyInputState();
            Assert.Equal(AnalogPollResult.Ok, s.Pass(kb.Io, output, null));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.W));
            Assert.Equal(0, kb.Io.PendingInput);
        }

        [Fact]
        public void SparkLink_Pass_FailedRowsKeepTheKeys_SixInARowEndTheSession()
        {
            // A failed pass counts toward six and pauses 15 ms, and the keys
            // stay until their rows expire (backend_sparklink.inc:1320-1327).
            var kb = new SparkKeyboard(Mg75MaxFactory) { Route = row => row == 2 ? Row((2, 1750)) : Row() };
            var s = StartedSpark(kb, new TestClock());
            var output = new AnalogKeyInputState();
            for (int pass = 0; pass < 6; pass++) Assert.Equal(AnalogPollResult.Ok, s.Pass(kb.Io, output, null));
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));

            kb.Route = _ => null;
            for (int miss = 1; miss < SparkLinkProtocol.FailStreakLimit; miss++)
            {
                Assert.Equal(AnalogPollResult.Idle, s.Pass(kb.Io, output, null));
                Assert.Equal(0.5f, output.Get(AnalogKeyCodes.W));
            }
            Assert.Equal(AnalogPollResult.Failed, s.Pass(kb.Io, output, null));
        }

        [Fact]
        public void SparkLink_Pass_AnAnswerResetsTheFailStreak()
        {
            // failStreak returns to 0 on any answered row (backend_sparklink.inc:1329).
            var kb = new SparkKeyboard(Mg75MaxFactory);
            var s = StartedSpark(kb, new TestClock());
            var output = new AnalogKeyInputState();
            for (int round = 0; round < 3; round++)
            {
                kb.Route = _ => null;
                for (int miss = 0; miss < SparkLinkProtocol.FailStreakLimit - 1; miss++)
                    Assert.Equal(AnalogPollResult.Idle, s.Pass(kb.Io, output, null));
                kb.Route = _ => Row();
                Assert.Equal(AnalogPollResult.Ok, s.Pass(kb.Io, output, null));
            }
        }

        [Fact]
        public void SparkLink_Pass_ARowThatStopsAnsweringDropsItsKeysAfter2160ms()
        {
            // Per-row freshness (backend_sparklink.inc:238-298): W on row 0
            // keeps answering, A on row 1 stops. A holds while its row is
            // fresh and drops once 2160 ms have passed, while W goes on.
            var layout = new[] { Row((0, AnalogKeyCodes.W)), Row((0, AnalogKeyCodes.A)) };
            bool row1Answers = true;
            var kb = new SparkKeyboard(layout) { Route = row => row == 0 || row1Answers ? Row((0, 3500)) : null };
            var clock = new TestClock();
            var s = StartedSpark(kb, clock);
            var output = new AnalogKeyInputState();
            Assert.Equal(AnalogPollResult.Ok, s.Pass(kb.Io, output, null));   // row 0
            long row1Asked = clock.Now;
            Assert.Equal(AnalogPollResult.Ok, s.Pass(kb.Io, output, null));   // row 1
            Assert.Equal(1f, output.Get(AnalogKeyCodes.A));

            // Each pass checks freshness as it starts, so the pass's start
            // time is what decides.
            row1Answers = false;
            long droppedAt = 0;
            for (int pass = 0; pass < 20 && droppedAt == 0; pass++)
            {
                clock.Now += 300;
                long start = clock.Now;
                var result = s.Pass(kb.Io, output, null);
                Assert.NotEqual(AnalogPollResult.Failed, result);
                Assert.Equal(1f, output.Get(AnalogKeyCodes.W));
                if (output.Get(AnalogKeyCodes.A) == 0f) droppedAt = start;
                else Assert.True(start - row1Asked <= SparkLinkProtocol.RowFreshnessMs + 5);
            }
            Assert.True(droppedAt - row1Asked > SparkLinkProtocol.RowFreshnessMs);
        }

        [Fact]
        public void SparkLink_Pass_NoAnsweredRowFor1800msEndsTheSession()
        {
            // SparkTickHotplug (backend_sparklink.inc:1821-1834): only after a
            // first answered row, and only past 1800 ms.
            var kb = new SparkKeyboard(Mg75MaxFactory);
            var clock = new TestClock();
            var s = StartedSpark(kb, clock);
            var output = new AnalogKeyInputState();
            clock.Now += 5000;   // no row answered yet: no stale stop
            Assert.Equal(AnalogPollResult.Ok, s.Pass(kb.Io, output, null));
            int writes = kb.Io.Log.Count;
            clock.Now += SparkLinkProtocol.NoPacketRestartMs;
            Assert.Equal(AnalogPollResult.Failed, s.Pass(kb.Io, output, null));
            Assert.Equal(writes, kb.Io.Log.Count);
        }

        [Fact]
        public void SparkLink_Pass_GoneDevice()
        {
            // Writes that fail are failed passes, six end the worker
            // (backend_sparklink.inc:693-694, :1320-1324).
            var kb = new SparkKeyboard(Mg75MaxFactory);
            var s = StartedSpark(kb, new TestClock());
            var output = new AnalogKeyInputState();
            kb.Io.Gone = true;
            for (int miss = 1; miss < SparkLinkProtocol.FailStreakLimit; miss++)
                Assert.Equal(AnalogPollResult.Idle, s.Pass(kb.Io, output, null));
            Assert.Equal(AnalogPollResult.Failed, s.Pass(kb.Io, output, null));

            // A read error ends the session at once.
            var io = new SwitchTransport { FeatureLength = 0 };
            var kb2 = new SparkKeyboard(Mg75MaxFactory);
            io.Respond = kb2.Respond;
            var s2 = new SparkLinkSession(0x0529, new TestClock().Read);
            Assert.True(s2.Start(io));
            io.ReadFails = true;
            Assert.Equal(AnalogPollResult.Failed, s2.Pass(io, output, null));
        }

        [Fact]
        public void SparkLink_Stop_SendsNothing()
        {
            // SparkStopLocked writes nothing to the keyboard (backend_sparklink.inc:1465-1563).
            var kb = new SparkKeyboard(Mg75MaxFactory);
            var s = StartedSpark(kb, new TestClock());
            s.Pass(kb.Io, new AnalogKeyInputState(), null);
            int writes = kb.Io.Log.Count;
            s.Stop(kb.Io);
            Assert.Equal(writes, kb.Io.Log.Count);
        }

        [Fact]
        public void SparkLink_Session_IgnoresHeldKeys()
        {
            // HallJoy asks rows in turn whatever Windows sees down (:1283-1317).
            var kb = new SparkKeyboard(Mg75MaxFactory);
            var s = StartedSpark(kb, new TestClock());
            int before = kb.Io.Writes("out").Count;
            s.Pass(kb.Io, new AnalogKeyInputState(), code => code == AnalogKeyCodes.Fn);
            Assert.Equal(Wire(0x04, 0x03, 0x01, 0x00), kb.Io.Writes("out")[before]);
        }

        // ── SparkLink write fallbacks ─────────────────────────────────────

        [Fact]
        public void SparkLink_WritePacket_FallsBackInHallJoysOrder()
        {
            // SparkWritePacket (backend_sparklink.inc:630-680) on 65-byte
            // reports: WriteFile A and B (the same bytes), then
            // HidD_SetOutputReport A and B, then HidD_SetFeature A and B.
            var payload = SparkLinkProtocol.RouteRowRequest(3);
            var wire = Wire(0x04, 0x03, 0x01, 0x03);

            var io = new SwitchTransport { SendWorks = false };
            Assert.True(SparkLinkProtocol.WritePacket(io, payload));
            Assert.Equal(new[] { "out", "out", "ctl" }, io.Log.Select(l => l.Kind));
            Assert.All(io.Log, l => Assert.Equal(wire, l.Data));

            io = new SwitchTransport { SendWorks = false, OutputReportWorks = false };
            Assert.True(SparkLinkProtocol.WritePacket(io, payload));
            Assert.Equal(new[] { "out", "out", "ctl", "ctl", "setf" }, io.Log.Select(l => l.Kind));

            io = new SwitchTransport { SendWorks = false, OutputReportWorks = false, FeatureWorks = false };
            Assert.False(SparkLinkProtocol.WritePacket(io, payload));
            Assert.Equal(new[] { "out", "out", "ctl", "ctl", "setf", "setf" }, io.Log.Select(l => l.Kind));

            // No feature report: the feature fallbacks are not made.
            io = new SwitchTransport { SendWorks = false, OutputReportWorks = false, FeatureLength = 0 };
            Assert.False(SparkLinkProtocol.WritePacket(io, payload));
            Assert.Equal(new[] { "out", "out", "ctl", "ctl" }, io.Log.Select(l => l.Kind));
        }

        [Fact]
        public void SparkLink_WritePacket_BufferShapesFollowTheReportLength()
        {
            // A 64-byte report takes the payload at byte 0 (backend_sparklink.inc:652).
            // B (65 bytes) fits no 64-byte report, so it is never sent.
            var payload = SparkLinkProtocol.DeviceInfoRequest();
            var io = new SwitchTransport { SendWorks = false, OutputReportWorks = false, OutputLength = 64, FeatureLength = 0 };
            Assert.False(SparkLinkProtocol.WritePacket(io, payload));
            Assert.Equal(new[] { "out", "ctl" }, io.Log.Select(l => l.Kind));
            Assert.All(io.Log, l => Assert.Equal(payload, l.Data));

            // A longer report takes A at its full length with the payload at
            // byte 1. B is shorter than the report, a call Windows fails, so
            // it is skipped.
            io = new SwitchTransport { SendWorks = false, OutputLength = 1024, FeatureLength = 0 };
            Assert.True(SparkLinkProtocol.WritePacket(io, payload));
            Assert.Equal(new[] { "out", "ctl" }, io.Log.Select(l => l.Kind));
            Assert.Equal(1024, io.Log[0].Data.Length);
            Assert.Equal(new byte[] { 0x00, 0x01, 0x02, 0x00 }, io.Log[0].Data.Take(4).ToArray());
        }

        // ── Sayo fixtures ─────────────────────────────────────────────────

        private static AnalogKeyboardDeviceInfo SayoInfo(ushort pid = SayoDepthProtocol.O3cProductId,
            ushort page = SayoDepthProtocol.UsagePage, ushort usage = SayoDepthProtocol.Usage,
            ushort inLen = 64, ushort outLen = 1024, ushort vid = SayoDepthProtocol.VendorId) => new()
        {
            VendorId = vid,
            ProductId = pid,
            UsagePage = page,
            Usage = usage,
            InputReportLength = inLen,
            OutputReportLength = outLen,
            Path = $@"\\?\hid#vid_{vid:x4}&pid_{pid:x4}&mi_02#8&1c2d3e4f&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
        };

        private static void Put(byte[] p, int at, int v)
        {
            p[at] = (byte)v;
            p[at + 1] = (byte)(v >> 8);
        }

        /// <summary>sayo_o3c_test.cpp:11-16: a reply frame for a command and
        /// index with the given payload, checksummed as the firmware does,
        /// cut to <paramref name="length"/> bytes as the input report arrives.</summary>
        private static byte[] SayoReply(int command, int index, byte[] payload, byte echo = SayoDepthProtocol.ConfigEcho,
            int length = 64)
        {
            var p = SayoDepthProtocol.ReadRequest((byte)command, (byte)index);
            p[1] = echo;
            Put(p, 4, payload.Length + 4);
            payload.CopyTo(p, 8);
            int sum = 0;
            for (int i = 0; i < 8 + payload.Length; i += 2)
                if (i != 2) sum += SayoDepthProtocol.U16(p, i);
            Put(p, 2, sum);
            return p.Take(length).ToArray();
        }

        private static byte[] DepthReply(int raw0, int raw1, int raw2, int length = 64)
        {
            var payload = new byte[6];
            Put(payload, 0, raw0);
            Put(payload, 2, raw1);
            Put(payload, 4, raw2);
            return SayoReply(SayoDepthProtocol.DepthCommand, SayoDepthProtocol.DepthIndex, payload,
                SayoDepthProtocol.DepthEcho, length);
        }

        /// <summary>A KeyInfo payload with the audited O3C geometry
        /// (sayo_o3c_test.cpp:22-23).</summary>
        private static byte[] KeyPayload(int index, int key, int modifiers = 0)
        {
            var k = new byte[56];
            k[0] = 1;
            Put(k, 4, 1000 + index * 2000);
            Put(k, 6, 3000);
            Put(k, 8, 1800);
            Put(k, 10, 1800);
            k[20] = (byte)modifiers;
            k[21] = (byte)key;
            return k;
        }

        /// <summary>A scripted O3C: the model answer, a KeyInfo payload per
        /// key (null for no answer) and depth frames for the polls.</summary>
        private sealed class SayoKeyboard
        {
            public byte[] Model = { 9, 0, 1, 0 };
            public byte[][] Keys = new byte[3][];
            public readonly Queue<byte[]> Depth = new();
            public readonly AnalogKeyboardTestTransport Io = new() { InputLength = 64, OutputLength = 1024, FeatureLength = 0 };

            public SayoKeyboard() => Io.OnSend = Respond;

            private IEnumerable<byte[]> Respond(byte[] report)
            {
                var answers = new List<byte[]>();
                if (report[1] == SayoDepthProtocol.DepthEcho)
                {
                    if (Depth.Count > 0) answers.Add(Depth.Dequeue());
                }
                else if (report[6] == SayoDepthProtocol.InfoCommand)
                {
                    if (Model != null) answers.Add(SayoReply(0, 0, Model));
                }
                else if (report[6] == SayoDepthProtocol.KeyInfoCommand)
                {
                    var key = Keys[report[7]];
                    if (key != null) answers.Add(SayoReply(0x10, report[7], key));
                }
                return answers;
            }
        }

        // ── Sayo identity and wire ────────────────────────────────────────

        [Fact]
        public void Sayo_Matches_TheOneCollectionHallJoyKeeps()
        {
            // backend_sayo.inc:641-649: FF12:2, output exactly 1024, input at
            // least 14. The O3C and any other SayoDevice PID pass the metadata
            // test, and the other PIDs still face the probe at Start.
            var route = SparkSayoRoutes.All[1];
            Assert.True(route.Matches(SayoInfo()));
            Assert.True(route.Matches(SayoInfo(inLen: 14)));
            Assert.True(route.Matches(SayoInfo(pid: 0x0010)));
            Assert.False(route.Matches(SayoInfo(inLen: 13)));
            Assert.False(route.Matches(SayoInfo(outLen: 1023)));
            Assert.False(route.Matches(SayoInfo(outLen: 1025)));
            Assert.False(route.Matches(SayoInfo(outLen: 65)));
            Assert.False(route.Matches(SayoInfo(usage: 1)));
            Assert.False(route.Matches(SayoInfo(page: 0xFF11)));
            Assert.False(route.Matches(SayoInfo(vid: 0x8088)));
            Assert.False(route.Matches(SayoInfo(vid: SparkLinkProtocol.VendorId)));
            Assert.False(route.Matches(null));
            // HallJoy names the O3C and leaves the others to their product string.
            Assert.Equal("SayoDevice O3C", route.Name(SayoInfo()));
            Assert.Null(route.Name(SayoInfo(pid: 0x0010)));
            Assert.Equal(new[] { AnalogKeyCodes.Z, AnalogKeyCodes.X, AnalogKeyCodes.C }, route.Keys(SayoInfo()));
        }

        [Fact]
        public void Sayo_Requests_ByteForByte()
        {
            // The depth poll (backend_sayo.inc:260-262), a full 1024-byte report.
            var poll = SayoDepthProtocol.DepthPollRequest();
            Assert.Equal(1024, poll.Length);
            Assert.Equal(new byte[] { 0x22, 0x12, 0x3C, 0x13, 0x05, 0x00, 0x15, 0x01 }, poll.Take(8).ToArray());
            Assert.All(poll.Skip(8), b => Assert.Equal(0, b));
            // It is itself a valid frame of length 5 (sayo_o3c_protocol.h:15-27).
            Assert.True(SayoDepthProtocol.Parse(poll, out var f));
            Assert.Equal(new byte[] { 0x00 }, f.Payload.ToArray());

            // so3c::Read (sayo_o3c_protocol.h:29-34): model info and KeyInfo 0 to 2.
            Assert.Equal(new byte[] { 0x22, 0x13, 0x26, 0x13, 0x04, 0x00, 0x00, 0x00 },
                SayoDepthProtocol.ReadRequest(0x00).Take(8).ToArray());
            Assert.Equal(new byte[] { 0x22, 0x13, 0x36, 0x13, 0x04, 0x00, 0x10, 0x00 },
                SayoDepthProtocol.ReadRequest(0x10, 0).Take(8).ToArray());
            Assert.Equal(new byte[] { 0x22, 0x13, 0x36, 0x14, 0x04, 0x00, 0x10, 0x01 },
                SayoDepthProtocol.ReadRequest(0x10, 1).Take(8).ToArray());
            Assert.Equal(new byte[] { 0x22, 0x13, 0x36, 0x15, 0x04, 0x00, 0x10, 0x02 },
                SayoDepthProtocol.ReadRequest(0x10, 2).Take(8).ToArray());
            Assert.Equal(1024, SayoDepthProtocol.ReadRequest(0x10, 2).Length);

            // sayo_o3c_test.cpp:19: the request parses with an empty payload.
            Assert.True(SayoDepthProtocol.Parse(SayoDepthProtocol.ReadRequest(0x10, 2), out f));
            Assert.Empty(f.Payload.ToArray());
            Assert.Equal(2, f.Index);
        }

        [Fact]
        public void Sayo_DepthFrame_HallJoysTestVector()
        {
            // sayo_o3c_test.cpp:37-39: raw 0, 2000, 4000 read 0, 500, 1000,
            // and the frame's first 14 bytes are the spec's Appendix B bytes.
            var depth = DepthReply(0, 2000, 4000, 1024);
            Assert.Equal(new byte[] { 0x22, 0x12, 0xB1, 0x2A, 0x0A, 0x00, 0x15, 0x01, 0x00, 0x00, 0xD0, 0x07, 0xA0, 0x0F },
                depth.Take(14).ToArray());
            Assert.True(SayoDepthProtocol.Parse(depth, out var f));
            Span<int> raw = stackalloc int[3];
            Assert.True(SayoDepthProtocol.Depth(f, raw));
            Assert.Equal(0, raw[0]);
            Assert.Equal(2000, raw[1]);
            Assert.Equal(4000, raw[2]);
            Assert.Equal(500, SayoDepthProtocol.Normalize(raw[1]));
            Assert.Equal(1000, SayoDepthProtocol.Normalize(raw[2]));
            Assert.False(SayoDepthProtocol.Binding(f, 0, out _));   // sayo_o3c_test.cpp:40
        }

        [Fact]
        public void Sayo_Parse_RejectsShortReadsAndEveryFlippedByte()
        {
            // sayo_o3c_test.cpp:41-44.
            var depth = DepthReply(0, 2000, 4000, 1024);
            for (int n = 0; n < 14; n++) Assert.False(SayoDepthProtocol.Parse(depth.AsSpan(0, n), out _), $"n={n}");
            Assert.True(SayoDepthProtocol.Parse(depth.AsSpan(0, 14), out _));
            foreach (int offset in new[] { 0, 1, 2, 4, 6, 7, 8, 12 })
            {
                var bad = (byte[])depth.Clone();
                bad[offset] ^= 1;
                Assert.False(SayoDepthProtocol.Parse(bad, out _), $"offset {offset}");
            }
            // Lengths past 1020 are continuation or error flags (sayo_o3c_protocol.h:19-20).
            var flagged = (byte[])depth.Clone();
            Put(flagged, 4, 1021);
            Assert.False(SayoDepthProtocol.Parse(flagged, out _));
        }

        [Fact]
        public void Sayo_Depth_NeedsItsOwnHeader_AndValuesUpTo8000()
        {
            // sayo_o3c_test.cpp:45-50 and sayo_o3c_protocol.h:59-66.
            var payload = new byte[] { 0, 0, 0xD0, 7, 0xA0, 15 };
            Span<int> raw = stackalloc int[3];
            Assert.True(SayoDepthProtocol.Parse(SayoReply(0x10, 1, payload, SayoDepthProtocol.DepthEcho), out var f));
            Assert.False(SayoDepthProtocol.Depth(f, raw));
            Assert.True(SayoDepthProtocol.Parse(SayoReply(0x15, 0, payload, SayoDepthProtocol.DepthEcho), out f));
            Assert.False(SayoDepthProtocol.Depth(f, raw));
            Assert.True(SayoDepthProtocol.Parse(SayoReply(0x15, 1, payload, SayoDepthProtocol.ConfigEcho), out f));
            Assert.False(SayoDepthProtocol.Depth(f, raw));
            Assert.True(SayoDepthProtocol.Parse(DepthReply(0, 8000, 0), out f));
            Assert.True(SayoDepthProtocol.Depth(f, raw));
            Assert.True(SayoDepthProtocol.Parse(DepthReply(0, 8001, 0), out f));
            Assert.False(SayoDepthProtocol.Depth(f, raw));
            // Five payload bytes is not a depth answer.
            Assert.True(SayoDepthProtocol.Parse(SayoReply(0x15, 1, new byte[5], SayoDepthProtocol.DepthEcho), out f));
            Assert.False(SayoDepthProtocol.Depth(f, raw));
        }

        [Fact]
        public void Sayo_Normalize_RestUnder14_FullAt4000()
        {
            // so3c::Normalize (sayo_o3c_protocol.h:67-70) and sayo_o3c_test.cpp:39, :55.
            Assert.Equal(0, SayoDepthProtocol.Normalize(0));
            Assert.Equal(0, SayoDepthProtocol.Normalize(12));
            Assert.Equal(0, SayoDepthProtocol.Normalize(13));
            Assert.Equal(4, SayoDepthProtocol.Normalize(14));
            Assert.Equal(250, SayoDepthProtocol.Normalize(1000));
            Assert.Equal(750, SayoDepthProtocol.Normalize(3000));
            Assert.Equal(1000, SayoDepthProtocol.Normalize(3998));
            Assert.Equal(1000, SayoDepthProtocol.Normalize(8000));
        }

        [Fact]
        public void Sayo_Binding_HallJoysKeyInfoCases()
        {
            // sayo_o3c_test.cpp:21-36, for each of the three keys.
            int[] factory = { AnalogKeyCodes.Z, AnalogKeyCodes.X, AnalogKeyCodes.C };
            for (int index = 0; index < 3; index++)
            {
                var key = KeyPayload(index, factory[index]);
                bool Read(out int hid)
                {
                    Assert.True(SayoDepthProtocol.Parse(SayoReply(0x10, index, key), out var f));
                    return SayoDepthProtocol.Binding(f, index, out hid);
                }
                Assert.True(Read(out int hid) && hid == factory[index]);
                key[21] = 0x1A; Assert.True(Read(out hid) && hid == 0x1A);
                key[20] = 1; Assert.False(Read(out _));                        // Ctrl+W is not W
                key[21] = 0; Assert.True(Read(out hid) && hid == 0xE0);
                key[20] = 0x80; Assert.True(Read(out hid) && hid == 0xE7);
                key[20] = 3; Assert.False(Read(out _));                        // two modifiers
                key[20] = 0; Assert.True(Read(out hid) && hid == 0);           // unassigned
                key[16] = 3; Assert.False(Read(out _));                        // mouse mode
                key[16] = 0; key[22] = 4; Assert.False(Read(out _));
                key[22] = 0; key[4] ^= 1; Assert.False(Read(out _));           // geometry
            }
            // A key alone must be a keyboard usage 0x04 to 0xE7 (sayo_o3c_protocol.h:54).
            Assert.True(SayoDepthProtocol.Parse(SayoReply(0x10, 0, KeyPayload(0, 0x03)), out var g));
            Assert.False(SayoDepthProtocol.Binding(g, 0, out _));
            Assert.True(SayoDepthProtocol.Parse(SayoReply(0x10, 0, KeyPayload(0, 0xE8)), out g));
            Assert.False(SayoDepthProtocol.Binding(g, 0, out _));
            // The answer must be for the key asked.
            Assert.True(SayoDepthProtocol.Parse(SayoReply(0x10, 1, KeyPayload(1, 0x1B)), out g));
            Assert.False(SayoDepthProtocol.Binding(g, 0, out _));
        }

        [Fact]
        public void Sayo_ChannelAnswers_AutomaticAndManual()
        {
            // sayo_o3c_test.cpp:73-78, with kO3cFirst = 0x470 (analog_key_codes.h:17).
            Assert.Equal(new[] { 0x470, 0x471, 0x472 }, SayoDepthProtocol.Channels);
            Assert.Equal(new[] { 0x1D, 0x1B, 0x06 }, SayoDepthProtocol.Factory);
            Assert.True(SayoDepthProtocol.ChannelAnswers(0, 0x470, true, 0));
            Assert.False(SayoDepthProtocol.ChannelAnswers(1, 0x470, true, 0));
            Assert.True(SayoDepthProtocol.ChannelAnswers(0, 9, true, 9));
            Assert.True(SayoDepthProtocol.ChannelAnswers(1, 9, true, 9));
            Assert.False(SayoDepthProtocol.ChannelAnswers(0, 9, false, 9));
            Assert.True(SayoDepthProtocol.ChannelAnswers(0, AnalogKeyCodes.Z, false, 9));
            Assert.False(SayoDepthProtocol.ChannelAnswers(0, 0x470, false, 9));
            Assert.False(SayoDepthProtocol.ChannelAnswers(0, 0, true, 0));
        }

        // ── Sayo Start ────────────────────────────────────────────────────

        [Fact]
        public void Sayo_O3c_Start_ReadsTheModelThenEachBinding()
        {
            // SayoReadConfiguration (backend_sayo.inc:202-251): model 9, then
            // KeyInfo 0 to 2. Key 0 is X, key 1 unassigned, key 2 Left Shift.
            var kb = new SayoKeyboard();
            kb.Keys[0] = KeyPayload(0, AnalogKeyCodes.X);
            kb.Keys[1] = KeyPayload(1, 0);
            kb.Keys[2] = KeyPayload(2, 0, modifiers: 0x02);
            var s = new SayoDepthSession(SayoDepthProtocol.O3cProductId, new TestClock().Read);
            Assert.True(s.Start(kb.Io));

            var writes = kb.Io.Writes("out");
            Assert.Equal(4, writes.Count);
            Assert.All(writes, w => Assert.Equal(1024, w.Length));
            Assert.Equal(SayoDepthProtocol.ReadRequest(0x00), writes[0]);
            for (int key = 0; key < 3; key++) Assert.Equal(SayoDepthProtocol.ReadRequest(0x10, (byte)key), writes[1 + key]);

            Assert.True(s.Automatic);
            Assert.True(s.ConfigurationRead);
            Assert.Equal(new[] { AnalogKeyCodes.X, 0, AnalogKeyCodes.LShift }, s.Bindings);
            Assert.Equal(new[] { 0x470, 0x471, 0x472, AnalogKeyCodes.X, AnalogKeyCodes.LShift }, s.KeyOrder);
            Assert.Equal("SayoDevice O3C", s.ModelName);
        }

        [Fact]
        public void Sayo_O3c_AnotherModel_KeepsTheManualLayout()
        {
            // A model answer other than 9 clears the O3C claim and skips the
            // KeyInfo reads (backend_sayo.inc:232-236).
            var kb = new SayoKeyboard { Model = new byte[] { 8, 0, 1, 0 } };
            var s = new SayoDepthSession(SayoDepthProtocol.O3cProductId, new TestClock().Read);
            Assert.True(s.Start(kb.Io));
            Assert.Single(kb.Io.Writes("out"));
            Assert.False(s.Automatic);
            Assert.Equal(new[] { AnalogKeyCodes.Z, AnalogKeyCodes.X, AnalogKeyCodes.C }, s.KeyOrder);

            kb.Depth.Enqueue(DepthReply(4000, 2000, 1000));
            var output = new AnalogKeyInputState();
            Assert.Equal(AnalogPollResult.Ok, s.Pass(kb.Io, output, null));
            Assert.Equal(1f, output.Get(AnalogKeyCodes.Z));
            Assert.Equal(0.5f, output.Get(AnalogKeyCodes.X));
            Assert.Equal(0.25f, output.Get(AnalogKeyCodes.C));
            Assert.Equal(3, output.Count);
        }

        [Fact]
        public void Sayo_O3c_NoModelAnswer_KeepsTheThreePhysicalKeys()
        {
            // A model read that times out keeps the O3C layout with no
            // bindings: two attempts, no KeyInfo (backend_sayo.inc:207-231).
            var kb = new SayoKeyboard { Model = null };
            var s = new SayoDepthSession(SayoDepthProtocol.O3cProductId, new TestClock().Read);
            Assert.True(s.Start(kb.Io));
            var writes = kb.Io.Writes("out");
            Assert.Equal(2, writes.Count);
            Assert.All(writes, w => Assert.Equal(SayoDepthProtocol.ReadRequest(0x00), w));
            Assert.True(s.Automatic);
            Assert.False(s.ConfigurationRead);
            Assert.Equal(new[] { 0x470, 0x471, 0x472 }, s.KeyOrder);
        }

        [Fact]
        public void Sayo_O3c_ARejectedBindingKeepsTheOthers()
        {
            // A chord or a missing answer leaves that key at binding 0
            // (backend_sayo.inc:237-247).
            var kb = new SayoKeyboard();
            kb.Keys[0] = KeyPayload(0, AnalogKeyCodes.Z);
            kb.Keys[1] = KeyPayload(1, 0x1A, modifiers: 0x01);
            kb.Keys[2] = null;
            var s = new SayoDepthSession(SayoDepthProtocol.O3cProductId, new TestClock().Read);
            Assert.True(s.Start(kb.Io));
            Assert.Equal(new[] { AnalogKeyCodes.Z, 0, 0 }, s.Bindings);
            Assert.True(s.ConfigurationRead);
            Assert.Equal(new[] { 0x470, 0x471, 0x472, AnalogKeyCodes.Z }, s.KeyOrder);
            // KeyInfo 2 went unanswered through both attempts.
            Assert.Equal(1 + 1 + 1 + 2, kb.Io.Writes("out").Count);
        }

        [Fact]
        public void Sayo_O3c_ShortInputReport_SkipsTheConfigurationRead()
        {
            // The reads need a 64-byte input report (backend_sayo.inc:204-205).
            var kb = new SayoKeyboard();
            kb.Io.InputLength = 32;
            var s = new SayoDepthSession(SayoDepthProtocol.O3cProductId, new TestClock().Read);
            Assert.True(s.Start(kb.Io));
            Assert.Empty(kb.Io.Log);
            Assert.False(s.Automatic);
            Assert.Equal(new[] { AnalogKeyCodes.Z, AnalogKeyCodes.X, AnalogKeyCodes.C }, s.KeyOrder);
        }

        [Fact]
        public void Sayo_O3c_ConfigurationReadDropsDepthFrames()
        {
            // Everything but the awaited answer is dropped, depth frames
            // included (backend_sayo.inc:220-221).
            var kb = new SayoKeyboard();
            kb.Io.QueueInput(DepthReply(4000, 4000, 4000), new byte[] { 0x21, 0, 0, 0, 0, 0, 0, 0, 0x10, 1 });
            kb.Keys[0] = KeyPayload(0, AnalogKeyCodes.Z);
            var s = new SayoDepthSession(SayoDepthProtocol.O3cProductId, new TestClock().Read);
            Assert.True(s.Start(kb.Io));
            Assert.True(s.Automatic);
            Assert.Equal(AnalogKeyCodes.Z, s.Bindings[0]);
        }

        [Fact]
        public void Sayo_O3c_GoneDuringTheConfigurationRead()
        {
            var io = new SwitchTransport { ReadFails = true, InputLength = 64, OutputLength = 1024 };
            Assert.False(new SayoDepthSession(SayoDepthProtocol.O3cProductId, new TestClock().Read).Start(io));
        }

        [Fact]
        public void Sayo_OtherPid_IsAdmittedOnlyByAValidDepthAnswer()
        {
            // SayoProbeDepthProtocol (backend_sayo.inc:269-303): one depth
            // poll, then 250 ms for a frame that passes Parse and Depth.
            var io = new AnalogKeyboardTestTransport { InputLength = 64, OutputLength = 1024, FeatureLength = 0 };
            io.OnSend = _ => new[] { DepthReply(0, 0, 0) };
            var s = new SayoDepthSession(0x0010, new TestClock().Read);
            Assert.True(s.Start(io));
            Assert.Single(io.Writes("out"));
            Assert.Equal(SayoDepthProtocol.DepthPollRequest(), io.Writes("out")[0]);
            Assert.False(s.Automatic);
            Assert.Null(s.ModelName);
            Assert.Equal(new[] { AnalogKeyCodes.Z, AnalogKeyCodes.X, AnalogKeyCodes.C }, s.KeyOrder);

            // Silence, a configuration answer or a bad checksum admits nothing.
            var silent = new AnalogKeyboardTestTransport { InputLength = 64, OutputLength = 1024 };
            Assert.False(new SayoDepthSession(0x0010, new TestClock().Read).Start(silent));
            var config = new AnalogKeyboardTestTransport { InputLength = 64, OutputLength = 1024 };
            config.OnSend = _ => new[] { SayoReply(0, 0, new byte[] { 9, 0, 1, 0 }) };
            Assert.False(new SayoDepthSession(0x0010, new TestClock().Read).Start(config));
            var corrupt = DepthReply(0, 0, 0);
            corrupt[2] ^= 0xFF;
            var bad = new AnalogKeyboardTestTransport { InputLength = 64, OutputLength = 1024 };
            bad.OnSend = _ => new[] { corrupt };
            Assert.False(new SayoDepthSession(0x0010, new TestClock().Read).Start(bad));
            Assert.Single(bad.Writes("out"));
        }

        // ── Sayo Pass ─────────────────────────────────────────────────────

        private static SayoDepthSession StartedO3c(SayoKeyboard kb, TestClock clock)
        {
            var s = new SayoDepthSession(SayoDepthProtocol.O3cProductId, clock.Read);
            Assert.True(s.Start(kb.Io));
            return s;
        }

        [Fact]
        public void Sayo_Pass_PublishesThePhysicalCodesAndTheBindings()
        {
            // backend.cpp:4484-4494 with sayo_o3c_test.cpp:51-53: bindings 9,
            // 9 and 10, duplicates aggregated by their largest depth.
            var kb = new SayoKeyboard();
            kb.Keys[0] = KeyPayload(0, 9);
            kb.Keys[1] = KeyPayload(1, 9);
            kb.Keys[2] = KeyPayload(2, 10);
            var clock = new TestClock();
            var s = StartedO3c(kb, clock);
            Assert.Equal(new[] { 0x470, 0x471, 0x472, 9, 10 }, s.KeyOrder);
            int before = kb.Io.Writes("out").Count;

            var output = new AnalogKeyInputState();
            kb.Depth.Enqueue(DepthReply(3000, 0, 2000));   // 750, 0, 500
            Assert.Equal(AnalogPollResult.Ok, s.Pass(kb.Io, output, null));
            Assert.Equal(SayoDepthProtocol.DepthPollRequest(), kb.Io.Writes("out")[before]);
            Assert.Equal(0.75f, output.Get(0x470));
            Assert.Equal(0f, output.Get(0x471));
            Assert.Equal(0.5f, output.Get(0x472));
            Assert.Equal(0.75f, output.Get(9));
            Assert.Equal(0.5f, output.Get(10));
            Assert.Equal(4, output.Count);

            kb.Depth.Enqueue(DepthReply(0, 1000, 2000));    // 0, 250, 500
            clock.Now += SayoDepthProtocol.PollIntervalMs;
            Assert.Equal(AnalogPollResult.Ok, s.Pass(kb.Io, output, null));
            Assert.Equal(0f, output.Get(0x470));
            Assert.Equal(0.25f, output.Get(0x471));
            Assert.Equal(0.25f, output.Get(9));
            Assert.Equal(0.5f, output.Get(10));
        }

        [Fact]
        public void Sayo_Pass_PollsAtMostEvery8ms()
        {
            // backend_sayo.inc:336-351: a poll when 8 ms have passed since the last.
            var kb = new SayoKeyboard();
            kb.Io.InputLength = 32;   // no configuration read, so Start sends nothing
            var clock = new TestClock { Step = 0 };
            var s = StartedO3c(kb, clock);
            var output = new AnalogKeyInputState();
            Assert.Equal(AnalogPollResult.Idle, s.Pass(kb.Io, output, null));
            Assert.Single(kb.Io.Writes("out"));
            clock.Now += 7;
            s.Pass(kb.Io, output, null);
            Assert.Single(kb.Io.Writes("out"));
            clock.Now += 1;
            s.Pass(kb.Io, output, null);
            Assert.Equal(2, kb.Io.Writes("out").Count);
        }

        [Fact]
        public void Sayo_Pass_OtherReportsAreNotDepth()
        {
            // Only a 0x22 frame that passes Parse and Depth publishes
            // (backend_sayo.inc:367, :178). An 0x21 edge report, a configuration
            // answer and a corrupt frame change nothing.
            var kb = new SayoKeyboard();
            var s = StartedO3c(kb, new TestClock());
            var output = new AnalogKeyInputState();
            var corrupt = DepthReply(4000, 4000, 4000);
            corrupt[10] ^= 1;
            kb.Io.QueueInput(
                new byte[] { 0x21, 0, 0, 0, 0, 0, 0, 0, 0x10, 0 },
                SayoReply(0x10, 0, KeyPayload(0, AnalogKeyCodes.Z)),
                corrupt);
            for (int i = 0; i < 3; i++) Assert.Equal(AnalogPollResult.Idle, s.Pass(kb.Io, output, null));
            Assert.Equal(0, output.Count);
        }

        [Fact]
        public void Sayo_Pass_DepthOlderThan160msReadsZero()
        {
            // backend.cpp:4486-4489.
            var kb = new SayoKeyboard();
            var clock = new TestClock();
            var s = StartedO3c(kb, clock);
            clock.Step = 0;
            var output = new AnalogKeyInputState();
            kb.Depth.Enqueue(DepthReply(4000, 0, 0));
            Assert.Equal(AnalogPollResult.Ok, s.Pass(kb.Io, output, null));
            Assert.Equal(1f, output.Get(0x470));
            clock.Now += 160;
            Assert.Equal(AnalogPollResult.Idle, s.Pass(kb.Io, output, null));
            Assert.Equal(1f, output.Get(0x470));
            clock.Now += 1;
            Assert.Equal(AnalogPollResult.Ok, s.Pass(kb.Io, output, null));
            Assert.Equal(0, output.Count);
            Assert.Equal(AnalogPollResult.Idle, s.Pass(kb.Io, output, null));
        }

        [Fact]
        public void Sayo_Pass_AnyReportKeepsTheSession_1800msOfSilenceEndsIt()
        {
            // SayoTickHotplug (backend_sayo.inc:1003-1015): the last report of
            // any kind, and never before the first one.
            var kb = new SayoKeyboard();
            var clock = new TestClock();
            var s = StartedO3c(kb, clock);
            clock.Step = 0;
            var output = new AnalogKeyInputState();
            clock.Now += 10_000;
            Assert.Equal(AnalogPollResult.Idle, s.Pass(kb.Io, output, null));

            kb.Io.QueueInput(new byte[] { 0x21, 0, 0, 0, 0, 0, 0, 0, 0x11, 2 });
            Assert.Equal(AnalogPollResult.Idle, s.Pass(kb.Io, output, null));
            clock.Now += SayoDepthProtocol.NoPacketRestartMs;
            Assert.Equal(AnalogPollResult.Idle, s.Pass(kb.Io, output, null));
            clock.Now += 1;
            Assert.Equal(AnalogPollResult.Failed, s.Pass(kb.Io, output, null));
        }

        [Fact]
        public void Sayo_Pass_WriteFailuresNeverEndTheLoop()
        {
            // Failed polls are counted and logged, never fatal (backend_sayo.inc:345-350).
            var io = new SwitchTransport { SendWorks = false, InputLength = 32, OutputLength = 1024 };
            var s = new SayoDepthSession(SayoDepthProtocol.O3cProductId, new TestClock { Step = 10 }.Read);
            Assert.True(s.Start(io));
            var output = new AnalogKeyInputState();
            for (int i = 0; i < 6; i++) Assert.Equal(AnalogPollResult.Idle, s.Pass(io, output, null));
            Assert.Equal(6, s.PollFailStreak);
            Assert.Equal(6, io.Log.Count);
            // A frame another client asked for still counts, while this
            // session's own polls keep failing (backend_sayo.inc:333-368).
            io.QueueInput(DepthReply(4000, 0, 0, 32));
            Assert.Equal(AnalogPollResult.Ok, s.Pass(io, output, null));
            Assert.Equal(7, s.PollFailStreak);
            Assert.Equal(1f, output.Get(AnalogKeyCodes.Z));
            // The next poll that goes out resets the count.
            io.SendWorks = true;
            Assert.Equal(AnalogPollResult.Idle, s.Pass(io, output, null));
            Assert.Equal(0, s.PollFailStreak);
        }

        [Fact]
        public void Sayo_Pass_GoneDevice_AndStopSendsNothing()
        {
            // A read error other than a timeout ends the reader (backend_sayo.inc:355-361),
            // and nothing is written at stop (:653-755).
            var kb = new SayoKeyboard();
            var s = StartedO3c(kb, new TestClock());
            var output = new AnalogKeyInputState();
            kb.Depth.Enqueue(DepthReply(2000, 0, 0));
            Assert.Equal(AnalogPollResult.Ok, s.Pass(kb.Io, output, null));
            int writes = kb.Io.Log.Count;
            s.Stop(kb.Io);
            Assert.Equal(writes, kb.Io.Log.Count);
            kb.Io.Gone = true;
            Assert.Equal(AnalogPollResult.Failed, s.Pass(kb.Io, output, null));
        }
    }
}
