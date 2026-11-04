using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using R = PadForge.Common.Input.Peripherals.RoyuanLighting;

namespace PadForge.Tests
{
    /// <summary>The Royuan family against OpenRGB's
    /// RoyuanKeyboardController at the pinned commit (#557).</summary>
    [Collection("PeripheralOutputStatics")]
    public class RoyuanLightingTests
    {
        private static string Hex(byte[] bytes, int count) => ReportLog.Hex(bytes, count);

        private static readonly IReadOnlyList<IHidLightingFamily> Families = new IHidLightingFamily[] { R.Instance };

        private static HidCollectionInfo Vendor(ushort pid, int iface)
            => KeyboardFamilyRig.Collection(0x3151, pid, 0xFF00, 0x01, iface: iface, feature: 65);

        [Fact]
        public void EveryRegistration_IsMatchedOnItsFeatureCollection()
        {
            var registrations = OpenRgbRegistrations.Of(R.FamilyName).ToList();
            Assert.Equal(5, registrations.Count);
            foreach (var r in registrations)
            {
                Assert.Equal(r.Name, R.Instance.Match(Vendor((ushort)r.ProductId, r.Interface)).Name);
                Assert.Null(R.Instance.Match(Vendor((ushort)r.ProductId, r.Interface) with { FeatureReportLength = 9 }));
            }
        }

        [Fact]
        public void TheChecksums_AreOpenRgbs()
        {
            // CalculateChecksum (RoyuanKeyboardController.cpp:249-271).
            var report = new byte[] { 0, 0x07, 0x01, 0x04, 0x03, 0x08, 0x10, 0x20, 0x30 };
            // Sum 0x07+1+4+3+8+0x10+0x20+0x30 = 0x77, ones' complement 0x88.
            Assert.Equal(0x88, R.Checksum(report, 1, 8, true));
            // Next power of two above 0x77 is 0x80: 0x80 - 0x77 - 1 = 8.
            Assert.Equal(0x08, R.Checksum(report, 1, 8, false));
            Assert.Equal(0x00, R.Checksum(new byte[9], 1, 8, false));
        }

        [Fact]
        public void TheReports_AreOpenRgbs()
        {
            // The Akko's Static: 07 01, speed 4, brightness 3, option 08.
            var akko = R.Picture(R.Board.Akko, 0x102030);
            Assert.Equal(65, akko.Length);
            Assert.Equal("00 07 01 04 03 08 10 20 30 88", Hex(akko, 10));
            // The Epomaker's Always On: speed 0, brightness 4, option 07,
            // sum 0x07+1+0+4+7+0x60 = 0x73, 0x80 - 0x73 - 1 = 0x0C.
            Assert.Equal("00 07 01 00 04 07 10 20 30 0C", Hex(R.Picture(R.Board.Legacy, 0x102030), 10));
            // The Skyloong's Direct mode and color (lines 379-427).
            Assert.Equal("00 07 15 04 04 05 10 20 30", Hex(R.SkyloongDirectMode(0x05, 0x102030), 9));
            var color = R.DirectColor(0x102030);
            // 0x0E + 0x60 = 0x6E, ones' complement 0x91 at byte 8.
            Assert.Equal("00 0E 10 20 30 00 00 00 91", Hex(color, 9));
            Assert.Equal("00 87 00 00 00 00 00 00 78", Hex(R.ReadRequest(true), 9));
        }

        [Fact]
        public void ARestore_IsWhatSendUpdateMakesOfTheReadState()
        {
            // Mode 05, speed byte 02, brightness 07 (cut to 4), option 13,
            // color 0A 0B 0C.
            var state = new byte[] { 0x05, 0x02, 0x07, 0x13, 0x0A, 0x0B, 0x0C };
            Assert.Equal("00 07 05 02 04 13 0A 0B 0C", Hex(R.RestoreReport(R.Board.Akko, state), 9));
            // A speed byte past 4 goes back as the default reversed.
            state[1] = 9;
            Assert.Equal(4, R.RestoreReport(R.Board.Akko, state)[3]);
            Assert.Equal(2, R.RestoreReport(R.Board.Skyloong, state)[3]);
            // The Skyloong's Static takes option 07.
            state[0] = 0x01;
            Assert.Equal(0x07, R.RestoreReport(R.Board.Skyloong, state)[5]);
            Assert.Equal(0x13, R.RestoreReport(R.Board.Akko, state)[5]);
        }

        private static byte[] Answer()
        {
            var a = new byte[65];
            a[1] = 0x87;
            a[2] = 0x05; a[3] = 0x02; a[4] = 0x03; a[5] = 0x13; a[6] = 0x0A; a[7] = 0x0B; a[8] = 0x0C;
            return a;
        }

        [Fact]
        public void AnAkkoClaim_ReadsItsState_HoldsAndSpaces_AndWritesItBack()
        {
            using var rig = new KeyboardFamilyRig { FeatureLength = 65 };
            int reads = 0;
            rig.Log.FeatureAnswer = (_, _) => ++reads < 2 ? new byte[65] : Answer();
            var collection = Vendor(0x4003, 0);
            using var backend = rig.Lit(Families, collection, 0xFF0000, holdMs: R.AkkoPreSetMs);
            var sent = rig.Log.Sent();
            Assert.Equal("00 87", Hex(sent[0].Report, 2));
            // The first read missed, the second answered, 80 ms after each ask.
            Assert.Equal(2, sent.Count(s => s.Kind == 'G'));
            Assert.Equal(2, rig.Waits.Count(w => w == R.ReadWaitMs));
            Assert.Equal("00 07 01 04 03 08 FF 00 00", Hex(rig.Log.Of('F')[1], 9));
            var restore = "00 07 05 02 03 13 0A 0B 0C";
            Assert.Equal(restore, Hex(Assert.Single(Assert.Single(backend.Recorded).Frames), 9));
            // A new color waits 1.5 s after the last.
            KeyboardFamilyRig.Light(0x00FF00);
            KeyboardFamilyRig.Passes(backend);
            rig.Advance(R.AkkoPreSetMs);
            KeyboardFamilyRig.Passes(backend);
            Assert.Equal(2, rig.Log.Of('F').Count);
            rig.Advance(R.AkkoPostSetMs);
            KeyboardFamilyRig.Passes(backend);
            Assert.Equal(3, rig.Log.Of('F').Count);
            rig.Log.Clear();
            rig.Release(backend);
            Assert.Equal(restore, Hex(Assert.Single(rig.Log.Of('F')), 9));
        }

        [Fact]
        public void ASkyloongClaim_SetsDirectOnce_ThenSendsColors()
        {
            using var rig = new KeyboardFamilyRig { FeatureLength = 65 };
            rig.Log.FeatureAnswer = (_, _) => Answer();
            var collection = Vendor(0x5029, 2);
            using var backend = rig.Lit(Families, collection, 0x0000FF);
            var features = rig.Log.Of('F');
            Assert.Equal("00 07 15 04 04 13 00 00 FF", Hex(features[1], 9));
            Assert.Equal("00 0E 00 00 FF", Hex(features[2], 5));
            KeyboardFamilyRig.Light(0x00FF00);
            KeyboardFamilyRig.Passes(backend);
            rig.Advance(R.RepaintMs);
            KeyboardFamilyRig.Passes(backend);
            Assert.Equal("00 0E 00 FF 00", Hex(rig.Log.Of('F')[^1], 5));
            Assert.Equal(4, rig.Log.Of('F').Count);
        }

        [Fact]
        public void ABoardThatDoesNotAnswer_GetsNoSession_AndTheEpomakerNeedsNoAnswer()
        {
            using (var rig = new KeyboardFamilyRig { FeatureLength = 65 })
            {
                using var backend = rig.Lit(Families, Vendor(0x4003, 0));
                Assert.True(backend.Snapshot.Units.Single().Asleep);
                Assert.Equal(5, rig.Log.Sent().Count(s => s.Kind == 'G'));
            }
            using (var rig = new KeyboardFamilyRig { FeatureLength = 65 })
            {
                using var backend = rig.Lit(Families, Vendor(0x4010, 2), 0x102030, holdMs: SinowealthLighting.StoredHoldMs);
                Assert.Equal("00 07 01 00 04 07 10 20 30 0C", Hex(Assert.Single(rig.Log.Of('F')), 10));
                Assert.Empty(backend.Recorded);
                rig.Log.Clear();
                rig.Release(backend);
                Assert.Empty(rig.Log.Sent());
            }
        }
    }
}
