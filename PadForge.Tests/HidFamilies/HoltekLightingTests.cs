using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;
using H = PadForge.Common.Input.Peripherals.HoltekLighting;

namespace PadForge.Tests
{
    /// <summary>The Holtek mouse and mat against OpenRGB's HoltekController
    /// at the pinned commit (#557). Every expected byte is written out here
    /// from OpenRGB.</summary>
    [Collection("PeripheralOutputStatics")]
    public class HoltekLightingTests
    {
        private static readonly IReadOnlyList<IHidLightingFamily> Families = new IHidLightingFamily[] { H.Instance };

        [Fact]
        public void BothRegistrations_AreMatched_EachByItsReportLength()
        {
            var registrations = OpenRgbRegistrations.Of(H.OpenRgbFamily).ToList();
            // HoltekA070ControllerDetect.cpp:43, HoltekA1FAControllerDetect.cpp:43.
            Assert.Equal(2, registrations.Count);
            var mouseR = registrations.Single(r => r.ProductId == 0xA070);
            var matR = registrations.Single(r => r.ProductId == 0xA1FA);
            var mouse = H.Instance.Match(CollectionFor(mouseR, feature: 8));
            var mat = H.Instance.Match(CollectionFor(matR, feature: 9));
            Assert.Equal("Holtek USB Gaming Mouse", mouse.Name);
            Assert.Equal("Holtek Mousemat", mat.Name);
            Assert.Same(mouse, HidLightingFamilies.Match(CollectionFor(mouseR, feature: 8)));
            Assert.Same(mat, HidLightingFamilies.Match(CollectionFor(matR, feature: 9)));
            Assert.Null(H.Instance.Match(CollectionFor(mouseR, feature: 7)));
            Assert.Null(H.Instance.Match(CollectionFor(matR, feature: 8)));
            // The mouse's color is not stored, the mat's may be.
            Assert.Equal(0, mouse.HoldMs);
            Assert.Equal(50, mouse.RepaintMs);
            Assert.Equal(LenovoLegionLighting.HoldMs, mat.HoldMs);
            Assert.Equal(LenovoLegionLighting.RepaintMs, mat.RepaintMs);
            Assert.Single(mouse.Map.Lights);
            Assert.Single(mat.Map.Lights);
            Assert.Equal("holtek:04D9:A1FA", mat.Map.Product);
            Assert.Empty(mouse.Programs);
            // Holtek's vendor ID is Redragon's too, and each family takes its own.
            Assert.Null(RedragonMouseLighting.Instance.Match(CollectionFor(mouseR, feature: 16)));
            Assert.Null(H.Instance.Match(KeyboardFamilyRig.Collection(0x04D9, 0xFC30, 0xFFA0, 1, iface: 2, feature: 16)));
        }

        [Fact]
        public void TheReports_AreSetRgbAndTheMatsStaticMode()
        {
            // HoltekA070Controller.cpp:62-88.
            Assert.Equal("07 0A 00 11 22 33 00 00", Hex(H.MouseFrame(0x112233)));
            // HoltekA1FAController.cpp:55-75 with RGBController_HoltekA1FA.cpp:107-118.
            Assert.Equal("00 08 00 20 00 00 11 22 33", Hex(H.MatFrame(0x112233)));
        }

        [Fact]
        public void TheMouse_IsPaintedAtOnce_AndTheSaveCommandNeverGoesOut()
        {
            using var rig = new KeyboardFamilyRig { FeatureLength = 8 };
            var collection = KeyboardFamilyRig.Collection(0x04D9, 0xA070, 0xFF00, 0x02, iface: 1, feature: 8);
            using var backend = rig.Lit(Families, collection, 0x445566);
            Assert.Equal("07 0A 00 44 55 66 00 00", Hex(Assert.Single(rig.Log.Of('F'))));
            rig.Log.Clear();
            KeyboardFamilyRig.Light(0x010203);
            KeyboardFamilyRig.Passes(backend);
            rig.Advance(H.MouseRepaintMs);
            KeyboardFamilyRig.Passes(backend);
            Assert.Equal("07 0A 00 01 02 03 00 00", Hex(Assert.Single(rig.Log.Of('F'))));
            Assert.DoesNotContain(rig.Log.Of('F'), f => f[1] == 0x0B);
            Assert.Empty(backend.Recorded);
            rig.Log.Clear();
            rig.Release(backend);
            Assert.Empty(rig.Log.Sent());
        }

        [Fact]
        public void TheMat_IsHeld_ThenPainted()
        {
            using var rig = new KeyboardFamilyRig { FeatureLength = 9 };
            var collection = KeyboardFamilyRig.Collection(0x04D9, 0xA1FA, 0xFF00, 0xFF00, iface: 2, feature: 9);
            using var backend = rig.Started(Families, collection);
            KeyboardFamilyRig.Link(KeyboardFamilyRig.PathOf(collection));
            KeyboardFamilyRig.Light(0x0000FF);
            KeyboardFamilyRig.Passes(backend);
            Assert.Empty(rig.Log.Sent());
            rig.Advance(H.MatHoldMs);
            KeyboardFamilyRig.Passes(backend);
            Assert.Equal("00 08 00 20 00 00 00 00 FF", Hex(Assert.Single(rig.Log.Of('F'))));
            rig.Log.Clear();
            rig.Release(backend);
            Assert.Empty(rig.Log.Sent());
        }
    }
}
