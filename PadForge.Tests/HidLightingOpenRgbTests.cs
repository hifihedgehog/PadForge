using System;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The shared worker beside OpenRGB (#557): while OpenRGB holds the
    /// keyboard, the linker closes the unit's path and names it taken, and
    /// the worker lets the keyboard go with nothing sent, a hand-back
    /// included, as it does for a program the model names.
    /// </summary>
    public partial class HidLightingBackendTests
    {
        private static LinkTable ThroughOpenRgb(bool taken) => new(new[]
        {
            new DeviceLinks
            {
                Device = Device,
                Lighting = new[] { new OutputPath(OutputFamily.OpenRgbDevice, "openrgb") },
                TakenByOpenRgb = taken ? new[] { PathOf(Spectrum) } : Array.Empty<OutputPath>(),
            },
        });

        [Fact]
        public void APathOpenRgbTakes_IsLetGoWithNoHandBack()
        {
            using var backend = Lit();
            int before = _keyboard.Calls().Count;
            PeripheralOutputs.PublishLinks(ThroughOpenRgb(taken: true));
            Passes(backend);
            Assert.Equal(before, _keyboard.Calls().Count);
            Assert.True(Channel().Disposed);
            Assert.Empty(backend.Recorded);
            // The picture stays, and what it held stays owed.
            Assert.Equal(Picture(5, 0x112233), _keyboard.Stored(5));
            Assert.NotNull(KeyboardProfileBackups.Get(Product));

            // While the path stays taken, what is owed waits too.
            Unlight();
            Advance(HidLightingBackend.RepairGraceMs);
            Passes(backend);
            Assert.Equal(before, _keyboard.Calls().Count);
            Assert.NotNull(KeyboardProfileBackups.Get(Product));
        }

        /// <summary>The same table with no mark: the path left for another
        /// reason, and the keyboard is handed back.</summary>
        [Fact]
        public void APathThatLeavesUnmarked_IsHandedBack()
        {
            using var backend = Lit();
            PeripheralOutputs.PublishLinks(ThroughOpenRgb(taken: false));
            Passes(backend);
            Assert.Equal(Original5(), LenovoLegionLighting.ContentsOf(Answer(_keyboard.Stored(5)), 5));
            Assert.Null(KeyboardProfileBackups.Get(Product));
        }
    }
}
