using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;
using M = PadForge.Common.Input.Peripherals.HyperXMousematLighting;

namespace PadForge.Tests
{
    /// <summary>The HyperX mousepad family against OpenRGB's
    /// HyperXMousematController at the pinned commit (#557). Every expected
    /// byte is written out here from OpenRGB.</summary>
    [Collection("PeripheralOutputStatics")]
    public class HyperXMousematLightingTests
    {
        private static readonly IReadOnlyList<IHidLightingFamily> Families = new IHidLightingFamily[] { M.Instance };

        private static HidCollectionInfo Of(int vid, int pid)
            => CollectionFor(OpenRgbRegistrations.Of(M.OpenRgbFamily).First(r => r.VendorId == vid && r.ProductId == pid),
                feature: 65);

        [Fact]
        public void EveryWindowsRegistration_IsMatched_ByItsFeatureCollection()
        {
            var registrations = OpenRgbRegistrations.Of(M.OpenRgbFamily).ToList();
            // HyperXMousematControllerDetect.cpp:48-52, the _WIN32 branch of the XL.
            Assert.Equal(3, registrations.Count);
            Assert.All(registrations, r => Assert.True(r.IsWrapped));
            foreach (var r in registrations)
            {
                var model = M.Instance.Match(CollectionFor(r, feature: 65));
                Assert.True(model != null, r.Name);
                Assert.Equal(r.Name, model.Name);
                Assert.Same(model, HidLightingFamilies.Match(CollectionFor(r, feature: 65)));
                Assert.Equal(new[] { VendorPrograms.Ngenuity }, model.Programs);
                // RGBController_HyperXMousemat.cpp:138-151.
                Assert.Equal(50, model.KeepAliveMs);
                Assert.Equal(50, model.KeepAliveIdleMs);
                // A collection of the interface without the 65-byte feature
                // report is not the one that takes the frames.
                Assert.Null(M.Instance.Match(CollectionFor(r, feature: 64)));
            }
            // The Fury Ultra's registration takes any page on interface 0.
            Assert.NotNull(M.Instance.Match(Of(0x0951, 0x1705) with { UsagePage = 0x000C, Usage = 1 }));
            Assert.Null(M.Instance.Match(Of(0x0951, 0x1705) with
            {
                InterfaceNumber = 1, Path = Of(0x0951, 0x1705).Path.Replace("mi_00", "mi_01"),
            }));
            // The Pulsefire Mat's names its page and usage.
            Assert.Null(M.Instance.Match(Of(0x03F0, 0x0F8D) with { UsagePage = 0xFF00 }));
        }

        [Fact]
        public void TheLights_AreTheUnderglowAndTheStrip_OrTheXlsTwo()
        {
            // HyperXMousematControllerDetect.cpp:36-37: 15 and 5, or 2 and 0.
            Assert.Equal(20, M.Instance.Match(Of(0x0951, 0x1705)).Map.Lights.Count);
            Assert.Equal(20, M.Instance.Match(Of(0x03F0, 0x0F8D)).Map.Lights.Count);
            var xl = M.Instance.Match(Of(0x0951, 0x1741)).Map;
            Assert.Equal(2, xl.Lights.Count);
            Assert.All(xl.Lights, l => Assert.Equal(LightKind.Edge, l.Kind));
            Assert.Equal(2, xl.EdgeCount);
            Assert.Equal("hyperx:0951:1741", xl.Product);
        }

        [Fact]
        public void APicture_IsTheStartThenSixteenEntriesTwice_BlackPastTheMatsLeds()
        {
            var model = M.Instance.Match(Of(0x0951, 0x1705));
            var io = new FakeReportChannel { FeatureReportLength = 65 };
            var session = model.Begin(io, _ => { });
            var colors = Enumerable.Range(1, 20).Select(i => i << 16 | (i + 0x40) << 8 | (i + 0x80)).ToArray();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, colors));
            var sent = Calls(io);
            Assert.Equal(3, sent.Count);
            Assert.All(sent, s => Assert.Equal("set", s.Kind));
            // HyperXMousematController.cpp:71-80.
            Assert.Equal(Hex(Bytes(65, (1, 0x04), (2, 0xF2), (9, 0x02))), Hex(sent[0].Bytes));
            // Lines 90-103: LEDs 1 to 16 as 81 RR GG BB.
            Assert.Equal("00 81 01 41 81 81 02 42 82", Hex(sent[1].Bytes, 9));
            Assert.Equal("81 10 50 90", Hex(sent[1].Bytes.Skip(61).ToArray()));
            // Lines 113-123: LEDs 17 to 20, then black entries to 32.
            Assert.Equal("00 81 11 51 91 81 12 52 92 81 13 53 93 81 14 54 94 81 00 00 00", Hex(sent[2].Bytes, 21));
            Assert.Equal("81 00 00 00", Hex(sent[2].Bytes.Skip(61).ToArray()));
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(3, io.Calls.Count);
            Assert.Empty(session.CrashFrames);
        }

        [Fact]
        public void AClaim_IsKeptAliveEvery50Ms_AndTheReleaseSendsNothing()
        {
            using var rig = new KeyboardFamilyRig();
            var collection = KeyboardFamilyRig.Collection(0x03F0, 0x0F8D, 0xFF90, 0xFF00, iface: 1, feature: 65);
            using var backend = rig.Lit(Families, collection, 0x0A0B0C);
            Assert.Equal(3, rig.Log.Of('F').Count);
            Assert.Equal("00 81 0A 0B 0C", Hex(rig.Log.Of('F')[1], 5));
            rig.Advance(M.KeepAliveMs - 1);
            KeyboardFamilyRig.Passes(backend);
            Assert.Equal(3, rig.Log.Of('F').Count);
            rig.Advance(1);
            KeyboardFamilyRig.Passes(backend);
            Assert.Equal(6, rig.Log.Of('F').Count);
            Assert.Empty(backend.Recorded);
            rig.Log.Clear();
            rig.Release(backend);
            Assert.Empty(rig.Log.Sent());
        }
    }
}
