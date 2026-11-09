using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;
using A = PadForge.Common.Input.Peripherals.AresonLighting;

namespace PadForge.Tests
{
    /// <summary>The Areson mice against OpenRGB's AresonController at the
    /// pinned commit (#557). Every expected byte is written out here from
    /// OpenRGB.</summary>
    [Collection("PeripheralOutputStatics")]
    public class AresonLightingTests
    {
        private static readonly IReadOnlyList<IHidLightingFamily> Families = new IHidLightingFamily[] { A.Instance };

        [Fact]
        public void EveryRegistration_IsMatched_AsAStoredOneLightMouse()
        {
            var registrations = OpenRgbRegistrations.Of(A.OpenRgbFamily).ToList();
            // AresonControllerDetect.cpp:51-58 at b9d903b5, the M901P-KS pair from fc3aa079.
            Assert.Equal(8, registrations.Count);
            foreach (var r in registrations)
            {
                var collection = CollectionFor(r, feature: 17);
                var model = A.Instance.Match(collection);
                Assert.True(model != null, r.Name);
                Assert.Equal(r.Name, model.Name);
                Assert.Same(model, HidLightingFamilies.Match(collection));
                Assert.Equal(LenovoLegionLighting.HoldMs, model.HoldMs);
                Assert.Equal(LenovoLegionLighting.RepaintMs, model.RepaintMs);
                Assert.Equal(0, model.KeepAliveMs);
                Assert.Empty(model.Programs);
                Assert.Single(model.Map.Lights);
                Assert.Null(A.Instance.Match(collection with { FeatureReportLength = 16 }));
                Assert.Null(A.Instance.Match(collection with { Usage = 1 }));
            }
        }

        [Fact]
        public void APicture_IsTheStaticMode_WithOpenRgbsCheckByte()
        {
            // AresonController.cpp:79-140: 08 07 00 00 A0 07, mode 02, the
            // color, speed 0, brightness FF, 0x55 less bytes 6 to 11, 4A at 16.
            // 0x55 - 0x02 - 0x11 - 0x22 - 0x33 - 0x00 - 0xFF = -0x112, 0xEE as a byte.
            Assert.Equal("08 07 00 00 A0 07 02 11 22 33 00 FF EE 00 00 00 4A", Hex(A.Frame(0x112233)));
            // Black: 0x55 - 0x02 - 0xFF = -0xAC, 0x54.
            Assert.Equal("08 07 00 00 A0 07 02 00 00 00 00 FF 54 00 00 00 4A", Hex(A.Frame(0)));
        }

        [Fact]
        public void AClaim_IsHeld_SentOnce_AndHandsBackNothing()
        {
            using var rig = new KeyboardFamilyRig { FeatureLength = 17 };
            var collection = KeyboardFamilyRig.Collection(0x25A7, 0xFA40, 0xFF02, 0x02, iface: 1, feature: 17);
            using var backend = rig.Started(Families, collection);
            KeyboardFamilyRig.Link(KeyboardFamilyRig.PathOf(collection));
            KeyboardFamilyRig.Light(0x112233);
            KeyboardFamilyRig.Passes(backend);
            Assert.Empty(rig.Log.Sent());
            rig.Advance(A.HoldMs);
            KeyboardFamilyRig.Passes(backend);
            Assert.Equal(Hex(A.Frame(0x112233)), Hex(Assert.Single(rig.Log.Of('F'))));
            Assert.Empty(backend.Recorded);
            rig.Log.Clear();
            rig.Release(backend);
            Assert.Empty(rig.Log.Sent());
        }
    }
}
