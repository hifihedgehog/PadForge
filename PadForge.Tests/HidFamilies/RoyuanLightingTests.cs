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

        private static HidCollectionInfo Vendor(ushort pid, int iface, ushort page = 0xFF00)
            => KeyboardFamilyRig.Collection(0x3151, pid, page, 0x01, iface: iface, feature: 65);

        [Fact]
        public void EveryRegistration_IsMatchedOnItsFeatureCollection()
        {
            // RoyuanKeyboardControllerDetect.cpp:125-130 at 6b681e3c: the Akko's vendor
            // collection on interface 1, page FFFF, is a registration of its own (d91bff77).
            var registrations = OpenRgbRegistrations.Of(R.FamilyName).ToList();
            Assert.Equal(6, registrations.Count);
            foreach (var r in registrations)
            {
                var collection = Vendor((ushort)r.ProductId, r.Interface, r.UsagePage < 0 ? (ushort)0xFF00 : (ushort)r.UsagePage);
                Assert.Equal(r.Name, R.Instance.Match(collection).Name);
                Assert.Null(R.Instance.Match(collection with { FeatureReportLength = 9 }));
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
            // The Akko's Static: 07 01, speed 4, brightness 4 (its most, since
            // PadForge dims the color itself), and ROYUAN_AKKO_COLOR_SINGLE, 07 since
            // d91bff77 swapped it with RANDOM (RoyuanKeyboardController.h:54-55 at
            // 6b681e3c). Sum 0x07+1+4+4+7+0x60 = 0x77, ones' complement 0x88.
            var akko = R.Picture(R.Board.Akko, 0x102030);
            Assert.Equal(65, akko.Length);
            Assert.Equal("00 07 01 04 04 07 10 20 30 88", Hex(akko, 10));
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
            // A Static state goes back with option 07 on both, whatever was read:
            // solid_option_overrides_static (SendUpdate, RoyuanKeyboardController.cpp:346-353),
            // which the Akko's profile sets since d91bff77 (lines 67-68).
            state[0] = 0x01;
            Assert.Equal(0x07, R.RestoreReport(R.Board.Skyloong, state)[5]);
            Assert.Equal(0x07, R.RestoreReport(R.Board.Akko, state)[5]);
            Assert.Equal(0x13, R.RestoreReport(R.Board.Legacy, state)[5]);
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
            // The family keeps the keyboard's next transfer by the rig's clock.
            using var backend = rig.Lit(new IHidLightingFamily[] { new R(rig.Tick) }, collection, 0xFF0000, holdMs: R.AkkoPreSetMs);
            var sent = rig.Log.Sent();
            Assert.Equal("00 87", Hex(sent[0].Report, 2));
            // The first read missed, the second answered, 80 ms after each ask.
            Assert.Equal(2, sent.Count(s => s.Kind == 'G'));
            Assert.Equal(2, rig.Waits.Count(w => w == R.ReadWaitMs));
            Assert.Equal("00 07 01 04 04 07 FF 00 00", Hex(rig.Log.Of('F')[1], 9));
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

        /// <summary>The Dukharo VN66 shares the Akko's product ID. OpenRGB makes no
        /// controller of its interface 0, whose product string names it, and lights it
        /// through the vendor collection on interface 1 with the Akko's profile
        /// (DetectRoyuanAkkoKeyboardControllers, RoyuanKeyboardControllerDetect.cpp:27-81,
        /// RoyuanKeyboardController.cpp:98-105, d91bff77).</summary>
        [Theory]
        [InlineData("ROYUAN Dukharo VN66 Wired")]
        [InlineData("VN66")]
        public void ADukharoVn66_IsLitOnItsVendorCollection_AndNotOnInterface0(string product)
        {
            byte[] answer = new byte[65];
            answer[1] = 0x87;
            answer[2] = 0x05; answer[3] = 0x02; answer[4] = 0x03; answer[5] = 0x13;
            long now = 0;
            var family = new R(() => now);
            var first = family.Match(Vendor(0x4003, 0));
            Assert.Equal("Akko 3068B Plus / PC75B Plus", first.Name);
            var io = new FakeReportChannel { Path = null, FeatureReportLength = 65, ProductString = product, Feature = _ => answer };
            Assert.Null(first.Begin(io, _ => { }));
            Assert.Empty(io.Calls);
            var vendor = family.Match(Vendor(0x4003, 1, 0xFFFF));
            Assert.Equal("Akko 3068B Plus / Dukharo VN66 (Vendor)", vendor.Name);
            var lit = new FakeReportChannel { Path = null, FeatureReportLength = 65, ProductString = product, Feature = _ => answer };
            var session = vendor.Begin(lit, _ => { });
            Assert.NotNull(session);
            // The first picture goes out 500 ms after the state's read.
            now += R.AkkoPreSetMs;
            Assert.Equal(HidLightingPaint.Sent, session.Paint(lit, _ => { }, vendor.Map.Lights, new[] { 0x102030 }));
            Assert.Equal("00 07 01 04 04 07 10 20 30 88", Hex(lit.Calls.Last().Bytes, 10));
            // An Akko on interface 0 opens as before.
            var akko = new FakeReportChannel { Path = null, FeatureReportLength = 65, ProductString = "Akko 3068B Plus", Feature = _ => answer };
            Assert.NotNull(first.Begin(akko, _ => { }));
        }

        /// <summary>A plain Akko 3068B Plus is lit on interface 0 and on its vendor
        /// collection, both of which OpenRGB registers (RoyuanKeyboardControllerDetect.cpp:125-126
        /// at 6b681e3c), and every feature report of either reaches its one control endpoint.
        /// The two collections of one Windows container keep one next transfer, set as
        /// OpenRGB sets next_transfer: 500 ms after the state's read and 1.5 s after a SET
        /// (RoyuanKeyboardController.cpp:19-24, 159 and 523). A picture asked for sooner on
        /// the other collection goes out once that time comes. Two keyboards are paced
        /// apart.</summary>
        [Fact]
        public void AnAkkosTwoCollections_KeepOneSpacing()
        {
            long now = 100_000;
            var answer = Answer();
            var keyboard = new Guid("0a0b0c0d-0000-0000-0000-000000000001");
            var family = new R(() => now, path => path.Contains("#k1", StringComparison.Ordinal) ? keyboard : Guid.Empty);
            var model0 = family.Match(Vendor(0x4003, 0));
            var model1 = family.Match(Vendor(0x4003, 1, 0xFFFF));
            FakeReportChannel Channel(string path) => new() { Path = path, FeatureReportLength = 65, Feature = _ => answer };
            int Sets(FakeReportChannel io) => io.Calls.Count(c => c.Kind == "set" && c.Bytes[1] == R.SetLedParams);
            var colors = new[] { 0x102030 };

            var first = Channel(@"\\?\hid#vid_3151&pid_4003&mi_00#k1");
            var second = Channel(@"\\?\hid#vid_3151&pid_4003&mi_01&col02#k1");
            var waits = new List<int>();
            var s0 = model0.Begin(first, waits.Add);
            Assert.NotNull(s0);
            // The second read waits out the 500 ms after the first.
            var s1 = model1.Begin(second, waits.Add);
            Assert.NotNull(s1);
            Assert.Contains(R.AkkoPreSetMs, waits);
            now += R.AkkoPreSetMs;
            Assert.Equal(HidLightingPaint.Sent, s0.Paint(first, _ => { }, model0.Map.Lights, colors));
            // The other collection's picture waits the 1.5 s after that SET.
            Assert.Equal(HidLightingPaint.Later, s1.Paint(second, _ => { }, model1.Map.Lights, colors));
            Assert.Equal(R.AkkoPostSetMs, s1.LaterMs);
            now += R.AkkoPostSetMs - 1;
            Assert.Equal(HidLightingPaint.Later, s1.Paint(second, _ => { }, model1.Map.Lights, colors));
            Assert.Equal(1, s1.LaterMs);
            Assert.Equal(0, Sets(second));
            now += 1;
            Assert.Equal(HidLightingPaint.Sent, s1.Paint(second, _ => { }, model1.Map.Lights, colors));
            Assert.Equal(1, Sets(second));
            // Its hand-back, right after, waits too.
            waits.Clear();
            Assert.True(s0.HandBack(first, waits.Add));
            Assert.Equal(new[] { R.AkkoPostSetMs }, waits);

            // Collections of two keyboards, which Windows names no container for, are paced apart.
            var other = Channel(@"\\?\hid#vid_3151&pid_4003&mi_00#k2");
            var s2 = model0.Begin(other, _ => { });
            now += R.AkkoPreSetMs;
            var third = Channel(@"\\?\hid#vid_3151&pid_4003&mi_01&col02#k3");
            var s3 = model1.Begin(third, _ => { });
            Assert.Equal(HidLightingPaint.Sent, s2.Paint(other, _ => { }, model0.Map.Lights, colors));
            now += R.AkkoPreSetMs;
            Assert.Equal(HidLightingPaint.Sent, s3.Paint(third, _ => { }, model1.Map.Lights, colors));
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
