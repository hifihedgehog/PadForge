using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using A = PadForge.Common.Input.Peripherals.AnnePro2Lighting;
using K = PadForge.Common.Input.Peripherals.RedSquareKeyroxLighting;

namespace PadForge.Tests
{
    /// <summary>The Anne Pro 2 and Red Square Keyrox families against OpenRGB
    /// at the pinned commit and Annemone (#557).</summary>
    [Collection("PeripheralOutputStatics")]
    public class AnnePro2KeyroxLightingTests
    {
        private static string Hex(byte[] bytes, int count) => ReportLog.Hex(bytes, count);
        private static string Hex(byte[] bytes, int from, int count) => ReportLog.Hex(bytes, from, count);

        [Fact]
        public void TheTables_AreOpenRgbsAtThePinnedCommit()
        {
            Assert.Equal("b9d903b544a40486709c4ca097d0b234b617db9c", AnnePro2KeyroxLayouts.OpenRgbCommit);
            // RGBController_AnnePro2.cpp:19-24 and 47-111.
            Assert.Equal(61, AnnePro2KeyroxLayouts.AnnePro2.Length);
            Assert.Equal(new AnneKeyroxLed("Key: Right Control", 60, 4, 12), AnnePro2KeyroxLayouts.AnnePro2[60]);
            // RGBController_RedSquareKeyrox.cpp:31-148: Enter is light 62 at
            // sequence place 50, Right Arrow last at 106.
            Assert.Equal(87, AnnePro2KeyroxLayouts.KeyroxTkl.Length);
            Assert.Equal(new AnneKeyroxLed("Key: Enter", 50, 3, 14), AnnePro2KeyroxLayouts.KeyroxTkl[62]);
            Assert.Equal(new AnneKeyroxLed("Key: Right Arrow", 106, 5, 17), AnnePro2KeyroxLayouts.KeyroxTkl[86]);
        }

        [Fact]
        public void EveryRegistration_IsMatched()
        {
            foreach (var r in OpenRgbRegistrations.Of(A.FamilyName))
            {
                var c = KeyboardFamilyRig.Collection((ushort)r.VendorId, (ushort)r.ProductId, 0xFF00, 1, iface: 1, output: 64);
                Assert.Equal("Anne Pro 2", A.Instance.Match(c).Name);
                Assert.Null(A.Instance.Match(c with { OutputReportLength = 2 }));
                Assert.Null(A.Instance.Match(c with { Path = c.Path.Replace("mi_01", "mi_00"), InterfaceNumber = 0 }));
            }
            Assert.Equal(5, OpenRgbRegistrations.Of(A.FamilyName).Count());
            foreach (var r in OpenRgbRegistrations.Of(K.FamilyName))
            {
                var c = KeyboardFamilyRig.Collection(0x1A2C, (ushort)r.ProductId, 0xFF00, 0x02, iface: 3, feature: 521);
                Assert.Equal(r.Name, K.Instance.Match(c).Name);
                Assert.Null(K.Instance.Match(c with { Usage = 1 }));
            }
            Assert.Equal(2, OpenRgbRegistrations.Of(K.FamilyName).Count());
        }

        [Fact]
        public void TheAnneFrame_LeavesOpenRgbsPlacesOut()
        {
            // Lines 196-214: places skip after 40, 41, 52, 53, 60 and two
            // after 55 and 56.
            Assert.Equal(40, A.PlaceOf(40));
            Assert.Equal(42, A.PlaceOf(41));
            Assert.Equal(44, A.PlaceOf(42));
            Assert.Equal(54, A.PlaceOf(52));
            Assert.Equal(56, A.PlaceOf(53));
            Assert.Equal(59, A.PlaceOf(55));
            Assert.Equal(62, A.PlaceOf(56));
            Assert.Equal(65, A.PlaceOf(57));
            Assert.Equal(68, A.PlaceOf(60));
            // The last light's place is 68 of the 70.
            var lights = A.Lights();
            var colors = Enumerable.Repeat(0x010203, 61).ToArray();
            var frame = A.Frame(lights, colors);
            Assert.Equal(210, frame.Length);
            Assert.Equal("01 02 03 00 00 00 01 02 03", Hex(frame, 40 * 3, 9));
        }

        [Fact]
        public void TheAnneReports_AreObinsKitsMultiColorMessage()
        {
            var frame = Enumerable.Range(0, 210).Select(i => (byte)i).ToArray();
            var reports = A.Reports(frame);
            // AnnePro2Controller.cpp:43-122: five reports, 0x50 + n, 55 then 10.
            Assert.Equal(5, reports.Count);
            Assert.All(reports, r => Assert.Equal(64, r.Length));
            Assert.Equal("00 7B 10 41 50 37 00 00 7D 20 03 FF 02 00 01", Hex(reports[0], 15));
            Assert.Equal("00 7B 10 41 54 0A", Hex(reports[4], 6));
            Assert.Equal(51, reports[1][13]);
            // The last carries bytes 204 to 209, then 0.
            Assert.Equal("CC CD CE CF D0 D1 00", Hex(reports[4], 13, 7));
        }

        [Fact]
        public void AnAnneClaim_SendsFiveReports50MsApart_AndHandsBackNothing()
        {
            using var rig = new KeyboardFamilyRig { OutputLength = 64 };
            var c = KeyboardFamilyRig.Collection(0x04D9, 0x8008, 0xFF00, 1, iface: 1, output: 64);
            using var backend = rig.Lit(new IHidLightingFamily[] { A.Instance }, c, 0xFF0000);
            Assert.Equal(5, rig.Log.Of('O').Count);
            Assert.Equal(5, rig.Waits.Count(w => w == A.ReportWaitMs));
            Assert.Equal("FF 00 00", Hex(rig.Log.Of('O')[0], 13, 3));
            rig.Log.Clear();
            rig.Release(backend);
            Assert.Empty(rig.Log.Sent());
        }

        [Fact]
        public void TheKeyroxReports_AreOpenRgbsCustomMode()
        {
            // RedSquareKeyroxController.cpp:106-122 and 223-269, report ID 0
            // ahead of the 520 bytes.
            var mode = K.ModeReport();
            Assert.Equal(521, mode.Length);
            Assert.Equal("00 00 00 00 00 01 00 04 00 0C", Hex(mode, 10));
            var lights = K.Lights();
            var colors = new int[lights.Count];
            colors[0] = 0x112233;   // Escape, place 0
            colors[86] = 0x445566;  // Right Arrow, place 106
            var report = K.ColorsReport(lights, colors);
            Assert.Equal("00 00 00 00 00 B0 01 07 00 11 22 33 FF", Hex(report, 13));
            Assert.Equal("44 55 66 FF", Hex(report, 1 + 7 + 106 * 4 + 1, 4));
        }

        [Fact]
        public void AKeyroxClaim_SetsCustomOnce()
        {
            using var rig = new KeyboardFamilyRig { FeatureLength = 521 };
            var c = KeyboardFamilyRig.Collection(0x1A2C, 0x1511, 0xFF00, 0x02, iface: 3, feature: 521);
            using var backend = rig.Lit(new IHidLightingFamily[] { K.Instance }, c, 0x0000FF);
            KeyboardFamilyRig.Light(0x00FF00);
            KeyboardFamilyRig.Passes(backend);
            rig.Advance(K.RepaintMs);
            KeyboardFamilyRig.Passes(backend);
            var features = rig.Log.Of('F');
            Assert.Equal(3, features.Count);
            Assert.Equal(0x0C, features[0][9]);
            Assert.Equal(0xB0, features[1][5]);
            Assert.Equal(0xFF, features[2][10]);
            rig.Log.Clear();
            rig.Release(backend);
            Assert.Empty(rig.Log.Sent());
        }
    }
}
