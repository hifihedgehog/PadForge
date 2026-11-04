using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The CLEVO light bar against OpenRGB's ClevoLightbarController
    /// (#557).</summary>
    public class ClevoLightbarLightingTests
    {
        [Fact]
        public void APicture_IsTheColorThenTheDirectMode()
        {
            var model = DeskLightKit.ModelOf(ClevoLightbarLighting.Instance, "ClevoLightbarController");
            Assert.Equal(LightKind.Edge, Assert.Single(model.Map.Lights).Kind);
            Assert.NotEqual(ClevoLighting.Instance.Match(CollectionFor(OpenRgbRegistrations.Of("ClevoLightbarController").Single(), feature: 8)),
                model);
            var (io, session) = DeskLightKit.Painted(model, new[] { 0x112233 });
            Assert.True(session.Feature);
            // SetColor and SetMode(1, 100, 0) (ClevoLightbarController.cpp:95-157):
            // the speed inverted, 10 + 1 - 0 = 0x0B.
            Assert.Equal(new[] { "14 00 01 11 22 33 00 00", "08 22 01 0B 64 01 00 00" }, Calls(io).Select(c => Hex(c.Bytes)));
            Assert.All(Calls(io), c => Assert.Equal("set", c.Kind));
            DeskLightKit.HandsBackNothing(io, session);
        }
    }

    /// <summary>The CLEVO light bar through the shared worker. No family
    /// before it in the worker's list, the other ITE families among them,
    /// takes its collection (#557).</summary>
    [Collection("PeripheralOutputStatics")]
    public class ClevoLightbarWorkerTests
    {
        [Fact]
        public void TheClevoLightbar_IsLitThroughTheWorker_ByFeatureReports()
        {
            using var rig = new KeyboardFamilyRig { FeatureLength = 8 };
            var c = KeyboardFamilyRig.Collection(0x048D, 0x7001, 0xFF03, 0x02, feature: 8);
            Assert.Same(ClevoLightbarLighting.Instance, HidLightingFamilies.Match(c)?.Family);
            using var backend = rig.Lit(new IHidLightingFamily[] { ClevoLightbarLighting.Instance }, c, 0x0000FF);
            Assert.Equal(new[] { "14 00 01 00 00 FF 00 00", "08 22 01 0B 64 01 00 00" }, rig.Log.Of('F').Select(f => Hex(f)));
        }
    }
}
