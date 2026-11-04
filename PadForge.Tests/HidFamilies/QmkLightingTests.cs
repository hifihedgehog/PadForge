using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using Q = PadForge.Common.Input.Peripherals.QmkLighting;

namespace PadForge.Tests
{
    /// <summary>The QMK family against OpenRGB's QMKController at the pinned
    /// commit, Keychron's QMK firmware and vial-qmk (#557). Every expected
    /// byte is written out from those sources, never from the builder under
    /// test.</summary>
    [Collection("PeripheralOutputStatics")]
    public class QmkLightingTests
    {
        private static string Hex(byte[] bytes, int count) => ReportLog.Hex(bytes, count);
        private static string Hex(byte[] bytes, int from, int count) => ReportLog.Hex(bytes, from, count);

        private static readonly IReadOnlyList<IHidLightingFamily> Families = new IHidLightingFamily[] { Q.Instance };

        private static HidCollectionInfo Raw(ushort vid, ushort pid, int iface = 1)
            => KeyboardFamilyRig.Collection(vid, pid, 0xFF60, 0x61, iface: iface, output: 33, input: 33);

        /// <summary>An input report: report ID 0, then the bytes, padded to 33.</summary>
        private static byte[] In(params byte[] bytes)
        {
            var report = new byte[33];
            Array.Copy(bytes, 0, report, 1, bytes.Length);
            return report;
        }

        [Fact]
        public void TheTables_AreOpenRgbsAtThePinnedCommit()
        {
            Assert.Equal("c6fc8016d215cca17d660c47746a08d7ea6a9175", QmkTables.OpenRgbCommit);
            // QMKKeycodes.cpp:16-151: KC_NO "", KC_TRANSPARENT "Key: Right Fn",
            // KC_A "Key: A", and the Space Cadet shifts 216 and 217.
            Assert.Equal("", Q.KeyName(0x0000));
            Assert.Equal("Key: Right Fn", Q.KeyName(0x0001));
            Assert.Equal("Key: A", Q.KeyName(0x0004));
            Assert.Equal("Key: Left Shift", Q.KeyName(216));
            Assert.Equal("Key: Right Windows", Q.KeyName(0x00E7));
            Assert.Equal("", Q.KeyName(0x5100));
            // QMKKeychronController_Devices.cpp:24-46: the Q1V2 ANSI patch,
            // seven cells, first 0,9 to 0,10.
            var q1 = Q.PatchOf(0x0106);
            Assert.Equal(7, q1.Length);
            Assert.Equal(new KeychronPatchCell(0, 9, 0, 10), q1[0]);
            Assert.Same(q1, Q.PatchOf(0x0107));
            Assert.Equal(394, QmkTables.KeychronPatches.Length);
            Assert.Null(Q.PatchOf(0x0001));
        }

        [Fact]
        public void TheRegistrations_ThatNameTheirDevices_AreMatched()
        {
            var registrations = OpenRgbRegistrations.Of(Q.FamilyName).ToList();
            // Five named ones and the two dynamic HID detectors whose IDs
            // come from the settings.
            Assert.Equal(7, registrations.Count);
            foreach (var r in registrations)
            {
                if (r.FromSettings || r.Transport != OpenRgbTransport.Hid)
                {
                    // Taken from the user's OpenRGB settings or registering
                    // others: nothing to match (QMKOpenRGBControllerDetect.cpp:172-178,
                    // QMKVialRGBControllerDetect.cpp:85-91).
                    continue;
                }
                ushort pid = (ushort)(r.ProductId == -1 ? 0x0110 : r.ProductId);
                var model = Q.Instance.Match(Raw((ushort)r.VendorId, pid));
                Assert.NotNull(model);
                Assert.Equal(r.Name, model.Name);
                // Another page of the same keyboard is not matched.
                Assert.Null(Q.Instance.Match(KeyboardFamilyRig.Collection((ushort)r.VendorId, pid, 0x0001, 0x06, iface: 0)));
            }
            Assert.Equal(3, registrations.Count(r => !r.FromSettings && r.Transport == OpenRgbTransport.Hid));
        }

        [Fact]
        public void TheHsv_IsOpenRgbsRgb2Hsv()
        {
            // qt/hsv.cpp:33-71, scaled hue (QMKKeychronController.cpp:591).
            Assert.Equal(((byte)0, (byte)255, (byte)255), Q.Hsv(0xFF0000));
            // Green: hue 120, 120 x 256 / 360 = 85.33.
            Assert.Equal(((byte)85, (byte)255, (byte)255), Q.Hsv(0x00FF00));
            // Blue: 240, 170.66.
            Assert.Equal(((byte)170, (byte)255, (byte)255), Q.Hsv(0x0000FF));
            // 0x804020: M 128, m 32, delta 96, hue (64 - 32) x 60 / 96 = 20,
            // 20 x 0.7111 = 14.2, saturation (24576 - 8) / 128 = 191.
            Assert.Equal(((byte)14, (byte)191, (byte)128), Q.Hsv(0x804020));
            // 0x640101: M 100, delta 99, saturation (25344 - 8) / 100 = 253.
            Assert.Equal(((byte)0, (byte)253, (byte)100), Q.Hsv(0x640101));
            // Gray has no hue.
            Assert.Equal(((byte)0, (byte)0, (byte)0x40), Q.Hsv(0x404040));
        }

        [Fact]
        public void TheKeychronReports_AreOpenRgbs()
        {
            // SetMode(0xFFFF): VIA custom set value, RGB matrix channel 3,
            // effect 2, 23, then Keychron RGB A8 per-key type 08 Solid 0
            // (QMKKeychronController.cpp:333-339 and 622-640).
            var direct = Q.KeychronDirect();
            Assert.Equal("00 07 03 02 17 00", Hex(direct[0], 6));
            Assert.Equal("00 A8 08 00 00", Hex(direct[1], 5));
            Assert.All(direct, d => Assert.Equal(33, d.Length));
            // Ten LEDs: nine, then one (lines 315-331), A8 0A start count
            // and HSV (lines 567-597).
            var colors = Enumerable.Repeat(0xFF0000, 10).ToArray();
            colors[9] = 0x0000FF;
            var frames = Q.KeychronFrames(10, colors);
            Assert.Equal(2, frames.Count);
            Assert.Equal("00 A8 0A 00 09 00 FF FF 00 FF FF", Hex(frames[0], 11));
            Assert.Equal("00 A8 0A 09 01 AA FF FF 00", Hex(frames[1], 9));
        }

        [Fact]
        public void TheVialReports_AreOpenRgbs()
        {
            // CmdSetMode: 07 41, mode low first, speed, H, S, V
            // (QMKVialRGBController.cpp:401-420).
            Assert.Equal("00 07 41 01 00 00 00 00 00", Hex(Q.VialMode(1, 0, 0, 0, 0), 9));
            Assert.Equal("00 07 41 2A 01 80 01 02 03", Hex(Q.VialMode(0x012A, 0x80, 1, 2, 3), 9));
            // CmdSendLEDs: 07 42, start low first, count, HSV (lines 366-399).
            var frames = Q.VialFrames(11, Enumerable.Repeat(0x00FF00, 11).ToArray());
            Assert.Equal(2, frames.Count);
            Assert.Equal("00 07 42 00 00 09 55 FF FF", Hex(frames[0], 9));
            Assert.Equal("00 07 42 09 00 02 55 FF FF 55 FF FF 00", Hex(frames[1], 13));
        }

        /// <summary>A Keychron keyboard on QMK: LED 0 on Escape at 0,0, LED 1
        /// on F1 at 0,1, LED 2 on a transparent key at 1,0, LED 3 on no
        /// key. It answers as Keychron's firmware does.</summary>
        private static byte[] Keychron(byte[] o)
        {
            byte cmd = o[1];
            switch (cmd)
            {
                case 0xB2: return In(0xB2, 0x00);
                case 0xA2: return In(0xA2, 0x00, 0x80, 0x00);
                case 0x04:
                {
                    int row = o[3], col = o[4];
                    ushort code = (row, col) switch { (0, 0) => 0x29, (0, 1) => 0x3A, (1, 0) => 0x01, _ => 0 };
                    return In(0x04, o[2], o[3], o[4], (byte)(code >> 8), (byte)code);
                }
                case 0x08:
                    return o[3] switch
                    {
                        0x02 => In(0x08, 0x03, 0x02, 0x05),
                        0x04 => In(0x08, 0x03, 0x04, 0x0A, 0xC8),
                        0x01 => In(0x08, 0x03, 0x01, 0x80),
                        0x03 => In(0x08, 0x03, 0x03, 0x40),
                        _ => In(0x08),
                    };
                case 0xA8:
                    switch (o[2])
                    {
                        case 0x01: return In(0xA8, 0x01, 0x00, 0x01);
                        case 0x05: return In(0xA8, 0x05, 0x00, 0x04);
                        case 0x06:
                        {
                            var cols = Enumerable.Repeat((byte)0xFF, 30).ToArray();
                            if (o[3] == 0) { cols[1] = 0; cols[2] = 1; }
                            if (o[3] == 1) cols[1] = 2;
                            return In(new byte[] { 0xA8, 0x06 }.Concat(cols).ToArray());
                        }
                        case 0x07: return In(0xA8, 0x07, 0x00, 0x02);
                    }
                    return In(o.Skip(1).Take(32).ToArray());
            }
            return In(o.Skip(1).Take(32).ToArray());
        }

        [Fact]
        public void AKeychronClaim_ReadsTheKeysFromTheBoard_LightsDirect_AndSetsItsEffectBack()
        {
            using var rig = new KeyboardFamilyRig { OutputLength = 33, InputLength = 33 };
            rig.Log.InputAnswer = Keychron;
            var collection = Raw(0x3434, 0x0106);
            using var backend = rig.Lit(Families, collection, 0x00FF00);
            var outputs = rig.Log.Of('O');
            // The reads: B2, A2, A8 01, A8 05, 32 rows of A8 06, three
            // keycodes, four values and the per-key type.
            Assert.Equal("00 B2", Hex(outputs[0], 2));
            Assert.Equal("00 A2", Hex(outputs[1], 2));
            Assert.Equal("00 A8 01", Hex(outputs[2], 3));
            Assert.Equal("00 A8 05", Hex(outputs[3], 3));
            Assert.Equal("00 A8 06 00 FF FF FF", Hex(outputs[4], 7));
            Assert.Equal("00 A8 06 1F FF FF FF", Hex(outputs[35], 7));
            Assert.Equal("00 04 00 00 00", Hex(outputs[36], 5));
            Assert.Equal("00 04 00 01 00", Hex(outputs[38], 5));
            Assert.Equal("00 08 03 02", Hex(outputs[39], 4));
            Assert.Equal("00 A8 07", Hex(outputs[43], 3));
            // Then Direct and one colors report of four LEDs.
            Assert.Equal("00 07 03 02 17", Hex(outputs[44], 5));
            Assert.Equal("00 A8 08 00", Hex(outputs[45], 4));
            Assert.Equal("00 A8 0A 00 04 55 FF FF 55 FF FF 55 FF FF 55 FF FF", Hex(outputs[46], 17));
            Assert.Equal(47, outputs.Count);
            // The map is the board's: Escape and F1 by their keycodes, the
            // transparent key labeled Fn, the fourth LED on no key.
            var map = PeripheralLightMaps.For(KeyboardFamilyRig.PathOf(collection));
            Assert.Equal(4, map.Lights.Count);
            Assert.Equal(0x29, map.Lights[0].Usage);
            Assert.Equal(LightKeySource.Device, map.Lights[0].KeySource);
            Assert.Equal(0x3A, map.Lights[1].Usage);
            Assert.Equal(LightLabel.Fn, map.Lights[2].Label);
            Assert.Equal(LightKind.Other, map.Lights[3].Kind);
            Assert.False(map.Lights[3].HasPosition);
            // The Q1V2 patch moves nothing at 0,0 or 0,1.
            Assert.Equal((0f, 0f), (map.Lights[0].X, map.Lights[0].Y));
            // The crash path and the hand-back set the effect read first:
            // per-key type 2, effect 5, color 0A C8, brightness 80, speed 40.
            var restore = new[] { "00 A8 08 02", "00 07 03 02 05", "00 07 03 04 0A C8", "00 07 03 01 80", "00 07 03 03 40" };
            var record = Assert.Single(backend.Recorded);
            Assert.False(record.Feature);
            Assert.Equal(restore, record.Frames.Select((f, i) => Hex(f, restore[i].Length / 3 + 1)));
            rig.Log.Clear();
            rig.Release(backend);
            var back = rig.Log.Of('O');
            Assert.Equal(restore, back.Select((f, i) => Hex(f, restore[i].Length / 3 + 1)));
        }

        [Fact]
        public void AKeychronBoardWithoutKeychronRgb_GetsNoSession()
        {
            using var rig = new KeyboardFamilyRig { OutputLength = 33, InputLength = 33 };
            rig.Log.InputAnswer = o => o[1] switch
            {
                0xA2 => In(0xA2, 0x00, 0x00, 0x00),
                0xA8 => In(0xA8, 0x01, 0x00, 0x00),
                _ => In(o[1]),
            };
            var collection = Raw(0x3434, 0x0110);
            using var backend = rig.Lit(Families, collection);
            Assert.True(backend.Snapshot.Units.Single().Asleep);
            Assert.DoesNotContain(rig.Log.Of('O'), o => o[1] == 0x07);
        }

        /// <summary>A VialRGB board: protocol 9, Vial 6 with the VialRGB
        /// flag, effects Off, Direct and Static, two LEDs, the first on A,
        /// the second underglow on no key, and mode 5.</summary>
        private static byte[] Vial(byte[] o)
        {
            switch (o[1])
            {
                case 0x01: return In(0x01, 0x00, 0x09);
                case 0xFE: return In(0x06, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 0x01);
                // A layered A, LT(1, KC_A) = 0x4104, high byte first as VIA
                // answers: OpenRGB's read takes its low byte and names A.
                case 0x04: return In(0x04, 0x00, o[3], o[4], 0x41, 0x04);
                case 0x08:
                    switch (o[2])
                    {
                        case 0x40: return In(0x08, 0x40, 0x01, 0x00, 0xFF);
                        case 0x42:
                            return o[3] == 0
                                ? In(new byte[] { 0x08, 0x42, 0x01, 0x00, 0x02, 0x00 }.Concat(Enumerable.Repeat((byte)0xFF, 26)).ToArray())
                                : In(new byte[] { 0x08, 0x42 }.Concat(Enumerable.Repeat((byte)0xFF, 30)).ToArray());
                        case 0x43: return In(0x08, 0x43, 0x02, 0x00);
                        case 0x44:
                            return o[3] == 0 ? In(0x08, 0x44, 10, 20, 0x04, 0, 0) : In(0x08, 0x44, 100, 60, 0x02, 0xFF, 0xFF);
                        case 0x41: return In(0x08, 0x41, 0x05, 0x00, 0x80, 0x01, 0x02, 0x03);
                    }
                    break;
            }
            return In(o.Skip(1).Take(32).ToArray());
        }

        [Fact]
        public void AVialClaim_ReadsTheBoard_SetsDirect_AndSetsTheModeBack()
        {
            using var rig = new KeyboardFamilyRig { OutputLength = 33, InputLength = 33 };
            rig.Log.InputAnswer = Vial;
            var collection = Raw(0x2E8A, 0x0011);
            using var backend = rig.Lit(Families, collection, 0xFF0000);
            var outputs = rig.Log.Of('O');
            Assert.Equal("00 01 00", Hex(outputs[0], 3));
            Assert.Equal("00 FE 00", Hex(outputs[1], 3));
            Assert.Equal("00 08 40", Hex(outputs[2], 3));
            Assert.Equal("00 08 42 00 00", Hex(outputs[3], 5));
            // The list had its end marker: no second request.
            Assert.Equal("00 08 43", Hex(outputs[4], 3));
            Assert.Equal("00 08 44 00 00", Hex(outputs[5], 5));
            Assert.Equal("00 04 00 00 00", Hex(outputs[6], 5));
            Assert.Equal("00 08 44 01 00", Hex(outputs[7], 5));
            Assert.Equal("00 04 00 FF FF", Hex(outputs[8], 5));
            Assert.Equal("00 08 41", Hex(outputs[9], 3));
            Assert.Equal("00 07 41 01 00 00 00 00 00", Hex(outputs[10], 9));
            Assert.Equal("00 07 42 00 00 02 00 FF FF 00 FF FF", Hex(outputs[11], 12));
            var map = PeripheralLightMaps.For(KeyboardFamilyRig.PathOf(collection));
            Assert.Equal(0x04, map.Lights[0].Usage);
            Assert.Equal((10f, 20f), (map.Lights[0].X, map.Lights[0].Y));
            Assert.Equal(LightKind.Edge, map.Lights[1].Kind);
            rig.Log.Clear();
            rig.Release(backend);
            Assert.Equal("00 07 41 05 00 80 01 02 03", Hex(Assert.Single(rig.Log.Of('O')), 9));
        }

        [Fact]
        public void AVialBoardWithoutDirect_GetsNoSession()
        {
            using var rig = new KeyboardFamilyRig { OutputLength = 33, InputLength = 33 };
            rig.Log.InputAnswer = o => o[1] == 0x08 && o[2] == 0x42
                ? In(new byte[] { 0x08, 0x42, 0x02, 0x00 }.Concat(Enumerable.Repeat((byte)0xFF, 28)).ToArray())
                : Vial(o);
            var collection = Raw(0x20A0, 0x422D);
            using var backend = rig.Lit(Families, collection);
            Assert.True(backend.Snapshot.Units.Single().Asleep);
        }
    }
}
