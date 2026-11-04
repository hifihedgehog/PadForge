using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;
using I = PadForge.Common.Input.Peripherals.InstantMouseLighting;

namespace PadForge.Tests
{
    /// <summary>The Instant mice against OpenRGB's InstantMouseController at
    /// the pinned commit (#557). Every expected byte is written out here from
    /// OpenRGB.</summary>
    [Collection("PeripheralOutputStatics")]
    public class InstantMouseLightingTests
    {
        private static readonly IReadOnlyList<IHidLightingFamily> Families = new IHidLightingFamily[] { I.Instance };

        [Fact]
        public void EveryRegistration_IsMatched_AsAOneLightMouse()
        {
            var registrations = OpenRgbRegistrations.Of(I.OpenRgbFamily).ToList();
            // InstantMouseControllerDetect.cpp:35-38.
            Assert.Equal(4, registrations.Count);
            foreach (var r in registrations)
            {
                var collection = CollectionFor(r, feature: 8);
                var model = I.Instance.Match(collection);
                Assert.True(model != null, r.Name);
                Assert.Equal(r.Name, model.Name);
                Assert.Same(model, HidLightingFamilies.Match(collection));
                Assert.Equal(LenovoLegionLighting.HoldMs, model.HoldMs);
                Assert.Equal(LenovoLegionLighting.RepaintMs, model.RepaintMs);
                Assert.Single(model.Map.Lights);
                Assert.Empty(model.Programs);
                Assert.Null(I.Instance.Match(collection with { FeatureReportLength = 7 }));
                Assert.Null(I.Instance.Match(collection with { UsagePage = 0xFF00 }));
            }
        }

        [Fact]
        public void TheReports_AreOpenRgbsDirectModeAndSixSlotColors()
        {
            // InstantMouseController.cpp:83-116 with the Direct mode 0x0A,
            // speed 0, brightness 7 and direction 0: 07 13 FF A0 0B 08 00 00.
            Assert.Equal("07 13 FF A0 0B 08 00 00", Hex(I.DirectModeFrame()));
            // Lines 57-81 for 0x12F0A5: red 0xF - 0x12/16 = 0xE, green
            // 0xF - 0xF0/16 = 0x0, blue 0xF - 0xA5/16 = 0x5.
            var frames = I.ColorFrames(0x12F0A5);
            Assert.Equal(new[]
            {
                "07 14 00 E5 00 00 00 00", "07 14 20 E5 00 00 00 00", "07 14 40 E5 00 00 00 00",
                "07 14 60 E5 00 00 00 00", "07 14 80 E5 00 00 00 00", "07 14 A0 E5 00 00 00 00",
            }, frames.Select(f => Hex(f)));
            // White is all zeros, black all 0xF.
            Assert.Equal("07 14 00 00 00 00 00 00", Hex(I.ColorFrames(0xFFFFFF)[0]));
            Assert.Equal("07 14 AF FF 00 00 00 00", Hex(I.ColorFrames(0)[5]));
        }

        [Fact]
        public void AClaim_SelectsDirectOnce_ThenSendsTheSlots_AndHandsBackNothing()
        {
            using var rig = new KeyboardFamilyRig { FeatureLength = 8 };
            var collection = KeyboardFamilyRig.Collection(0x30FA, 0x1030, 0xFF01, 0x01, iface: 1, feature: 8);
            using var backend = rig.Lit(Families, collection, 0xFF0000, holdMs: I.HoldMs);
            var sent = rig.Log.Of('F');
            Assert.Equal(7, sent.Count);
            Assert.Equal("07 13 FF A0 0B 08 00 00", Hex(sent[0]));
            Assert.Equal(I.ColorFrames(0xFF0000).Select(f => Hex(f)), sent.Skip(1).Select(f => Hex(f)));
            rig.Log.Clear();
            KeyboardFamilyRig.Light(0x00FF00);
            KeyboardFamilyRig.Passes(backend);
            rig.Advance(I.RepaintMs);
            KeyboardFamilyRig.Passes(backend);
            Assert.Equal(I.ColorFrames(0x00FF00).Select(f => Hex(f)), rig.Log.Of('F').Select(f => Hex(f)));
            Assert.Empty(backend.Recorded);
            rig.Log.Clear();
            rig.Release(backend);
            Assert.Empty(rig.Log.Sent());
        }
    }
}
