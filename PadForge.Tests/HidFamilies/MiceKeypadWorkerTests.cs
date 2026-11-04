using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>Two of the families through the shared worker, on a scripted
    /// device: a stored write is held, and the KP7's picture is kept alive
    /// (#557).</summary>
    [Collection("PeripheralOutputStatics")]
    public class MiceKeypadWorkerTests
    {
        [Fact]
        public void AStoredMouse_IsWrittenOnlyOnceThePictureHeld_AndHandedBackBySilence()
        {
            using var rig = new KeyboardFamilyRig { FeatureLength = 64 };
            var unit = KeyboardFamilyRig.Collection(0x145F, 0x0248, 0xFFA0, 0x01, iface: 1, feature: 64);
            using var backend = rig.Started(new IHidLightingFamily[] { TrustLighting.Instance }, unit);
            KeyboardFamilyRig.Link(KeyboardFamilyRig.PathOf(unit));
            KeyboardFamilyRig.Light(0x0A0B0C);
            KeyboardFamilyRig.Passes(backend);
            Assert.Empty(rig.Log.Sent());
            rig.Advance(SinowealthLighting.StoredHoldMs);
            KeyboardFamilyRig.Passes(backend);
            Assert.Equal(Hex(TrustLighting.Gxt180Frame(0x0A0B0C)), Hex(rig.Log.Of('F').Single()));
            rig.Log.Clear();
            rig.Release(backend);
            Assert.Empty(rig.Log.Sent());
            Assert.Empty(backend.Recorded);
        }

        [Fact]
        public void TheKp7_GetsItsPictureAgain_EveryFiftyMilliseconds()
        {
            using var rig = new KeyboardFamilyRig { OutputLength = 64, FeatureLength = 5 };
            var unit = KeyboardFamilyRig.Collection(0x10F5, 0x503A, 0xFF00, 0x01, iface: 4, feature: 5, output: 64);
            using var backend = rig.Lit(new IHidLightingFamily[] { TurtleBeachKP7Lighting.Instance }, unit, 0x112233);
            Assert.Equal("0E 00 01 00 00", Hex(rig.Log.Of('F').Single()));
            var pictures = rig.Log.Of('O');
            Assert.Equal(2, pictures.Count);
            rig.Log.Clear();
            rig.Advance(TurtleBeachKP7Lighting.KeepAliveMs);
            KeyboardFamilyRig.Passes(backend);
            var again = rig.Log.Of('O');
            Assert.True(again.Count >= 2);
            Assert.Equal(Hex(pictures[0]), Hex(again[0]));
            Assert.Equal(Hex(pictures[1]), Hex(again[1]));
            rig.Log.Clear();
            rig.Release(backend);
            Assert.Empty(rig.Log.Sent());
        }
    }
}
