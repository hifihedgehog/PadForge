using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using V = PadForge.Common.Input.Peripherals.ValkyrieKeyboardLighting;

namespace PadForge.Tests
{
    /// <summary>The Valkyrie family against OpenRGB's
    /// ValkyrieKeyboardController at the pinned commit (#557). Every
    /// expected byte is written out from OpenRGB.</summary>
    [Collection("PeripheralOutputStatics")]
    public class ValkyrieKeyboardLightingTests
    {
        private static string Hex(byte[] bytes, int count) => ReportLog.Hex(bytes, count);
        private static string Hex(byte[] bytes, int from, int count) => ReportLog.Hex(bytes, from, count);

        private static HidCollectionInfo Vendor(int iface) => KeyboardFamilyRig.Collection(0x05AC, 0x024F, 0xFF13, 0x0001, iface: iface, feature: 65);

        [Fact]
        public void TheTables_AreOpenRgbsAtThePinnedCommit()
        {
            Assert.Equal("b9d903b544a40486709c4ca097d0b234b617db9c", ValkyrieLayouts.OpenRgbCommit);
            // ValkyrieKeyboardController.h:55-74 and
            // RGBController_ValkyrieKeyboard.cpp:19-33 and 55-261.
            Assert.Equal(98, ValkyrieLayouts.Pro.Length);
            Assert.Equal(102, ValkyrieLayouts.Normal.Length);
            Assert.Equal(new ValkyrieLed("Key: Escape", 0x01, 0, 0), ValkyrieLayouts.Pro[0]);
            Assert.Equal(new ValkyrieLed("Key: Delete", 0x77, 0, 17), ValkyrieLayouts.Pro[13]);
            Assert.Equal(new ValkyrieLed("Key: Number Pad .", 0x69, 5, 20), ValkyrieLayouts.Pro[97]);
            Assert.Equal(new ValkyrieLed("Key: Print Screen", 0x70, 0, 18), ValkyrieLayouts.Normal[14]);
        }

        [Fact]
        public void EveryRegistration_IsMatched_AndTheInterfacePicksTheBoard()
        {
            var registrations = OpenRgbRegistrations.Of(V.FamilyName).ToList();
            Assert.Equal(2, registrations.Count);
            foreach (var r in registrations)
            {
                var model = V.Instance.Match(Vendor(r.Interface));
                Assert.Equal(r.Name, model.Name);
                Assert.Equal(r.Interface == 3 ? 98 : 102, model.Map.Lights.Count);
            }
            // The keyboard's own collection is not the lighting one.
            Assert.Null(V.Instance.Match(KeyboardFamilyRig.Collection(0x05AC, 0x024F, 0x0001, 0x06, iface: 0)));
            // "Key: +" is the = key.
            Assert.Equal(0x2E, V.Lights(true)[26].Usage);
        }

        [Fact]
        public void APicture_IsOpenRgbsSendColors()
        {
            using var rig = new KeyboardFamilyRig { FeatureLength = 65 };
            rig.Log.FeatureAnswer = (_, _) => new byte[65];
            var collection = Vendor(3);
            using var backend = rig.Lit(new IHidLightingFamily[] { V.Instance }, collection, 0x102030);
            var sent = rig.Log.Sent();
            // Start 04 20 .. 08, a read, seven color reports, the zero
            // report, the end 04 02, a read (ValkyrieKeyboardController.cpp:64-166).
            Assert.Equal("FGFFFFFFFFFG", new string(sent.Select(s => s.Kind).ToArray()));
            Assert.Equal("00 04 20 00 00 00 00 00 00 08 00", Hex(sent[0].Report, 11));
            Assert.Equal("00 01 10 20 30 02 10 20 30", Hex(sent[2].Report, 9));
            // The seventh carries the last two of 98 lights: 0x68 and 0x69.
            Assert.Equal("00 68 10 20 30 69 10 20 30 00", Hex(sent[8].Report, 10));
            Assert.All(sent.Where(s => s.Kind == 'F'), s => Assert.Equal(65, s.Report.Length));
            Assert.All(sent[9].Report, b => Assert.Equal(0, b));
            Assert.Equal("00 04 02 00", Hex(sent[10].Report, 4));
            // 1 ms after each of the ten reports and the two reads.
            Assert.Equal(12, rig.Waits.Count(w => w == V.ReportWaitMs));
            Assert.Contains(V.PictureWaitMs, rig.Waits);
            Assert.Empty(backend.Recorded);
            rig.Log.Clear();
            rig.Release(backend);
            Assert.Empty(rig.Log.Sent());
        }
    }
}
