using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;
using Q = PadForge.Common.Input.Peripherals.HyperXMicrophoneLighting;

namespace PadForge.Tests
{
    /// <summary>The QuadCast S and DuoCast family against OpenRGB's
    /// HyperXMicrophoneController at the pinned commit (#557). Every expected
    /// byte is written out here from OpenRGB.</summary>
    [Collection("PeripheralOutputStatics")]
    public class HyperXMicrophoneLightingTests
    {
        private static readonly IReadOnlyList<IHidLightingFamily> Families = new IHidLightingFamily[] { Q.Instance };

        [Fact]
        public void EveryRegistration_IsMatched_ByItsFeatureCollectionOnInterface0()
        {
            var registrations = OpenRgbRegistrations.Of(Q.OpenRgbFamily).ToList();
            // HyperXMicrophoneControllerDetect.cpp:52-59: seven QuadCast S and the DuoCast.
            Assert.Equal(8, registrations.Count);
            Assert.Equal(7, registrations.Count(r => r.Name == "HyperX Quadcast S"));
            Assert.Single(registrations, r => r.Name == "HyperX DuoCast" && r.ProductId == 0x098C);
            foreach (var r in registrations)
            {
                Assert.True(r.IsWrapped);
                Assert.Equal(0, r.Interface);
                var collection = CollectionFor(r, feature: 65);
                var model = Q.Instance.Match(collection);
                Assert.True(model != null, $"{r.Name} {r.ProductId:X4}");
                Assert.Equal(r.Name, model.Name);
                Assert.Same(model, HidLightingFamilies.Match(collection));
                Assert.Equal(new[] { VendorPrograms.Ngenuity }, model.Programs);
                // RGBController_HyperXMicrophone.cpp:141-151.
                Assert.Equal(50, model.KeepAliveMs);
                Assert.Equal(50, model.KeepAliveIdleMs);
                Assert.Equal($"hyperx:{r.VendorId:X4}:{r.ProductId:X4}", model.Map.Product);
                // Another collection of interface 0, any page, without the
                // 65-byte feature report, is not it.
                Assert.Null(Q.Instance.Match(collection with { FeatureReportLength = 9, UsagePage = 0x000C }));
                Assert.NotNull(Q.Instance.Match(collection with { UsagePage = 0xFF90, Usage = 0xFF00 }));
            }
            var lights = Q.Instance.Match(CollectionFor(registrations[0], feature: 65)).Map.Lights;
            // "Top", then "Bottom" (RGBController_HyperXMicrophone.cpp:90-112).
            Assert.Equal(new[] { 1, 2 }, lights.Select(l => l.Zone));
            Assert.All(lights, l => Assert.Equal(LightKind.Zone, l.Kind));
        }

        [Fact]
        public void APicture_IsTheColorReport_Then15Ms_ThenTheApply_Then15Ms()
        {
            var r = OpenRgbRegistrations.Of(Q.OpenRgbFamily).First();
            var model = Q.Instance.Match(CollectionFor(r, feature: 65));
            var io = new FakeReportChannel { FeatureReportLength = 65 };
            var session = model.Begin(io, _ => { });
            var waits = new List<int>();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, Recorder(waits), model.Map.Lights, new[] { 0x112233, 0x445566 }));
            var sent = Calls(io);
            Assert.Equal(2, sent.Count);
            // HyperXMicrophoneController.cpp:155-171 and 193-206.
            Assert.Equal(Hex(Bytes(65, (1, 0x81), (2, 0x11), (3, 0x22), (4, 0x33), (5, 0x81), (6, 0x44), (7, 0x55), (8, 0x66))),
                Hex(sent[0].Bytes));
            Assert.Equal(Hex(Bytes(65, (1, 0x04), (2, 0xF2), (9, 0x01))), Hex(sent[1].Bytes));
            Assert.All(sent, s => Assert.Equal("set", s.Kind));
            Assert.Equal(new[] { 15, 15 }, waits);
            // Register 0x53, the save (lines 61-136), is never written.
            Assert.DoesNotContain(sent, s => s.Bytes[1] == 0x04 && s.Bytes[2] == 0x53);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(2, io.Calls.Count);
            Assert.Empty(session.CrashFrames);
            io.FailWrites = true;
            Assert.Equal(HidLightingPaint.Failed, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0, 0 }));
        }

        [Fact]
        public void AClaim_IsKeptAlive_AndTheReleaseSendsNothing()
        {
            using var rig = new KeyboardFamilyRig();
            var collection = KeyboardFamilyRig.Collection(0x03F0, 0x0F8B, 0xFF90, 0xFF00, iface: 0, feature: 65);
            using var backend = rig.Lit(Families, collection, 0x00FF00);
            Assert.Equal(2, rig.Log.Of('F').Count);
            Assert.Equal("00 81 00 FF 00 81 00 FF 00", Hex(rig.Log.Of('F')[0], 9));
            // The waits of the picture moved the rig's clock 30 ms. The next
            // goes once 50 ms passed with nothing sent.
            Assert.Equal(new[] { 15, 15 }, rig.Waits);
            rig.Advance(Q.KeepAliveMs);
            KeyboardFamilyRig.Passes(backend);
            Assert.Equal(4, rig.Log.Of('F').Count);
            Assert.Empty(backend.Recorded);
            rig.Log.Clear();
            rig.Release(backend);
            Assert.Empty(rig.Log.Sent());
        }
    }
}
