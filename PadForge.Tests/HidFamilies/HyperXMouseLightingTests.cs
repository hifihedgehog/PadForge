using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;
using X = PadForge.Common.Input.Peripherals.HyperXMouseLighting;

namespace PadForge.Tests
{
    /// <summary>The HyperX mouse family against OpenRGB's
    /// HyperXMouseController at the pinned commit (#557). Every expected
    /// byte is written out here from OpenRGB, never from the builder under
    /// test.</summary>
    [Collection("PeripheralOutputStatics")]
    public class HyperXMouseLightingTests
    {
        private static readonly IReadOnlyList<IHidLightingFamily> Families = new IHidLightingFamily[] { X.Instance };

        private static HidCollectionInfo Of(int vid, int pid)
            => CollectionFor(OpenRgbRegistrations.Of(X.OpenRgbFamily).First(r => r.VendorId == vid && r.ProductId == pid),
                output: 65, feature: 264, input: 65);

        private static HidLightingModel ModelOf(int vid, int pid) => X.Instance.Match(Of(vid, pid));

        private static readonly Action<int> NoWait = _ => { };

        [Fact]
        public void EveryWindowsRegistration_IsMatched_WithItsProtocolAndKeepAlive()
        {
            var registrations = OpenRgbRegistrations.Of(X.OpenRgbFamily).ToList();
            // HyperXMouseControllerDetect.cpp:130-145, the _WIN32 branch of
            // the Haste's two.
            Assert.Equal(12, registrations.Count);
            foreach (var r in registrations)
            {
                var model = X.Instance.Match(CollectionFor(r, output: 65, feature: 264));
                Assert.True(model != null, r.Name);
                Assert.Equal(r.Name, model.Name);
                Assert.Same(model, HidLightingFamilies.Match(CollectionFor(r, output: 65, feature: 264)));
                Assert.Equal(new[] { VendorPrograms.Ngenuity }, model.Programs);
                // RGBController_HyperXPulsefireRaid.cpp:127 (1 s), no keep-alive
                // in the Dart's RGBController, 50 ms in the others'.
                int keepAlive = r.Detector switch
                {
                    "DetectHyperXPulsefireRaidControllers" => 1000,
                    "DetectHyperXPulsefireDartControllers" => 0,
                    _ => 50,
                };
                Assert.Equal(keepAlive, model.KeepAliveMs);
                Assert.Equal(keepAlive, model.KeepAliveIdleMs);
                Assert.Equal(0, model.HoldMs);
            }
            // The registrations' interfaces and pages: the Surge on 1 and
            // 0xFF01, the Dart (Wireless) on 2 and 0xFF00, the Haste on 3 and
            // 0xFF90, the Raid on usage 1 alone.
            Assert.Null(X.Instance.Match(Of(0x0951, 0x16D3) with { UsagePage = 0xFF00 }));
            Assert.Null(X.Instance.Match(Of(0x0951, 0x16E4) with { Usage = 2 }));
            Assert.NotNull(X.Instance.Match(Of(0x03F0, 0x0F8F)));
            Assert.NotNull(X.Instance.Match(Of(0x0951, 0x1727)));
            // A collection whose reports are too short for the frames is not
            // the lighting collection.
            Assert.Null(X.Instance.Match(Of(0x0951, 0x16D3) with { FeatureReportLength = 65 }));
            Assert.NotNull(X.Instance.Match(Of(0x0951, 0x1727) with { FeatureReportLength = 65 }));
            Assert.Null(X.Instance.Match(Of(0x0951, 0x1727) with { FeatureReportLength = 64 }));
            Assert.Null(X.Instance.Match(Of(0x0951, 0x16E2) with { OutputReportLength = 64 }));
            Assert.NotNull(X.Instance.Match(Of(0x0951, 0x16E2) with { FeatureReportLength = 0 }));
        }

        [Fact]
        public void TheLights_AreOpenRgbsLeds_InOrder()
        {
            // RGBController_HyperXPulsefireSurge.cpp:68-105: a strip of 32,
            // then the logo.
            var surge = ModelOf(0x0951, 0x16D3).Map.Lights;
            Assert.Equal(33, surge.Count);
            Assert.All(surge.Take(32), l => Assert.Equal(LightKind.Edge, l.Kind));
            Assert.Equal(LightKind.Logo, surge[32].Kind);
            Assert.Equal(Enumerable.Range(0, 33), surge.Select(l => l.Address));
            Assert.Equal(32, ModelOf(0x03F0, 0x0490).Map.EdgeCount);
            // The FPS Pro, the Core and the Haste: the logo alone.
            foreach (var (vid, pid) in new[] { (0x0951, 0x16D7), (0x0951, 0x16DE), (0x03F0, 0x0D8F), (0x03F0, 0x0F8F), (0x0951, 0x1727) })
            {
                var light = Assert.Single(ModelOf(vid, pid).Map.Lights);
                Assert.Equal(LightKind.Logo, light.Kind);
            }
            // The Raid and the Dart: "Scroll Wheel", then "Logo".
            foreach (var (vid, pid) in new[] { (0x0951, 0x16E4), (0x0951, 0x16E1), (0x03F0, 0x068E), (0x0951, 0x16E2), (0x03F0, 0x088E) })
            {
                var lights = ModelOf(vid, pid).Map.Lights;
                Assert.Equal(2, lights.Count);
                Assert.Equal(LightKind.Zone, lights[0].Kind);
                Assert.Equal(1, lights[0].Zone);
                Assert.Equal(LightKind.Logo, lights[1].Kind);
            }
            Assert.Equal("hyperx:03F0:0F8F", ModelOf(0x03F0, 0x0F8F).Map.Product);
        }

        [Fact]
        public void TheSurge_SendsItsStripAsRedsGreensAndBlues_AndTheLogoAt6C()
        {
            var colors = Enumerable.Range(0, 33).Select(i => (i + 1) << 16 | (i + 0x41) << 8 | (i + 0x81)).ToArray();
            var frame = X.SurgeFrame(colors);
            // HyperXPulsefireSurgeController.cpp:110-152.
            Assert.Equal(264, frame.Length);
            Assert.Equal("07 14 00 A0 00 00 00 00", Hex(frame, 8));
            for (int i = 0; i < 32; i++)
            {
                Assert.Equal(i + 1, frame[0x08 + i]);
                Assert.Equal(i + 0x41, frame[0x28 + i]);
                Assert.Equal(i + 0x81, frame[0x48 + i]);
            }
            Assert.Equal(new byte[] { 33, 0x61, 0xA1 }, frame.Skip(0x6C).Take(3).ToArray());
            Assert.All(frame.Skip(0x68).Take(4), b => Assert.Equal(0, b));
            Assert.All(frame.Skip(0x6F), b => Assert.Equal(0, b));
        }

        [Fact]
        public void TheFpsPro_AndTheRaid_SendTheirColorsWithA0AtByte8()
        {
            // HyperXPulsefireFPSProController.cpp:55-83.
            var fps = X.FpsProFrame(0x112233);
            Assert.Equal(264, fps.Length);
            Assert.Equal("07 0A 11 22 33 00 00 00 A0", Hex(fps, 9));
            Assert.All(fps.Skip(9), b => Assert.Equal(0, b));
            // HyperXPulsefireRaidController.cpp:53-71: the wheel, then the logo.
            var raid = X.RaidFrame(0x112233, 0x445566);
            Assert.Equal(264, raid.Length);
            Assert.Equal("07 0A 11 22 33 44 55 66 A0", Hex(raid, 9));
            Assert.All(raid.Skip(9), b => Assert.Equal(0, b));
        }

        [Fact]
        public void TheHaste_SendsSetupThenColor()
        {
            // HyperXPulsefireHasteController.cpp:64-116.
            var frames = X.HasteFrames(0xA1B2C3);
            Assert.Equal(2, frames.Length);
            Assert.Equal(Hex(Bytes(65, (1, 0x04), (2, 0xF2), (8, 0x02))), Hex(frames[0]));
            Assert.Equal(Hex(Bytes(65, (1, 0x81), (2, 0xA1), (3, 0xB2), (4, 0xC3), (8, 0x02))), Hex(frames[1]));
        }

        [Fact]
        public void TheDart_SendsTheWheelThenTheLogo_InStaticModeAtFullBrightness()
        {
            // HyperXPulsefireDartController.cpp:55-96, RGBController_HyperXPulsefireDart.cpp:149-160.
            Assert.Equal(Hex(Bytes(65, (1, 0xD2), (2, 0x10), (3, 0x00), (4, 0x08), (5, 0x11), (6, 0x22), (7, 0x33),
                (8, 0x11), (9, 0x22), (10, 0x33), (11, 0x64), (12, 0x00))), Hex(X.DartFrame(X.DartScroll, 0x112233)));
            var session = (X.Session)ModelOf(0x0951, 0x16E1).Begin(new FakeReportChannel(), NoWait);
            Assert.False(session.Feature);
            var model = ModelOf(0x0951, 0x16E1);
            var frames = session.PictureFrames(model.Map.Lights, new[] { 0x010203, 0x040506 });
            Assert.Equal(2, frames.Length);
            Assert.Equal("00 D2 10 00 08 01 02 03 01 02 03 64 00", Hex(frames[0], 13));
            Assert.Equal("00 D2 00 00 08 04 05 06 04 05 06 64 00", Hex(frames[1], 13));
            // The save, 00 DE FF (HyperXPulsefireDartController.cpp:98-122), is never one of them.
            Assert.DoesNotContain(frames, f => f[1] == 0xDE);
        }

        [Fact]
        public void EachSession_WritesItsReportKind_WaitsAsOpenRgbWaits_AndHandsBackNothing()
        {
            foreach (var (vid, pid, feature, waits) in new[]
            {
                (0x0951, 0x16D3, true, 0), (0x0951, 0x16D7, true, 0), (0x03F0, 0x0F8F, true, 0),
                (0x0951, 0x16E4, true, 10), (0x0951, 0x16E2, false, 2),
            })
            {
                var model = ModelOf(vid, pid);
                var io = new FakeReportChannel { FeatureReportLength = 264, OutputReportLength = 65 };
                var session = model.Begin(io, NoWait);
                Assert.Empty(io.Calls);
                var recorded = new List<int>();
                var colors = Distinct(model.Map.Lights.Count);
                Assert.Equal(HidLightingPaint.Sent, session.Paint(io, Recorder(recorded), model.Map.Lights, colors));
                var calls = Calls(io);
                Assert.All(calls, c => Assert.Equal(feature ? "set" : "out", c.Kind));
                Assert.Equal(((X.Session)session).PictureFrames(model.Map.Lights, colors).Select(f => Hex(f)),
                    calls.Select(c => Hex(c.Bytes)));
                if (waits == 0) Assert.Empty(recorded);
                else Assert.Equal(Enumerable.Repeat(waits, calls.Count), recorded);
                Assert.True(session.HandBack(io, NoWait));
                Assert.Equal(calls.Count, io.Calls.Count);
                Assert.Empty(session.CrashFrames);
                io.FailWrites = true;
                Assert.Equal(HidLightingPaint.Failed, session.Paint(io, NoWait, model.Map.Lights, colors));
            }
        }

        [Fact]
        public void ASurgeClaim_IsSentAndKeptAlive_AndTheReleaseSendsNothing()
        {
            using var rig = new KeyboardFamilyRig { FeatureLength = 264 };
            var collection = KeyboardFamilyRig.Collection(0x0951, 0x16D3, 0xFF01, 1, iface: 1, feature: 264);
            using var backend = rig.Lit(Families, collection, 0x102030);
            var first = Assert.Single(rig.Log.Of('F'));
            Assert.Equal("07 14 00 A0", Hex(first, 4));
            Assert.Equal(0x10, first[0x08]);
            Assert.Equal(0x30, first[0x48 + 31]);
            Assert.Equal("10 20 30", Hex(first.Skip(0x6C).Take(3).ToArray()));
            // The same picture again once none went out for 50 ms.
            rig.Advance(X.KeepAliveMs);
            KeyboardFamilyRig.Passes(backend);
            Assert.Equal(2, rig.Log.Of('F').Count);
            Assert.Equal(Hex(first), Hex(rig.Log.Of('F')[1]));
            Assert.Empty(backend.Recorded);
            rig.Log.Clear();
            rig.Release(backend);
            Assert.Empty(rig.Log.Sent());
        }
    }
}
