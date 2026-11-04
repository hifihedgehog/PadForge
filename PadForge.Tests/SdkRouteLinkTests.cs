using System;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The link pass's SDK routes for ASUS and MSI devices (#557 item 11):
    /// while Armoury Crate or Mystic Light runs and its SDK is installed, a
    /// device of its vendor is lit through the SDK's path of its kind, and
    /// is lit on its own once the program is closed. The two vendor rows
    /// take every path of their SDK.
    /// </summary>
    public class SdkRouteLinkTests
    {
        private static readonly Guid A = new("00000000-0000-0000-0000-00000000000a");
        private static readonly Guid Box = new("11111111-2222-3333-4444-555555555555");

        private static PeripheralPresence Presence(string running, bool aura = true, bool mysticLight = true)
            => PeripheralPresence.WithRunning(new PeripheralPresence(false, false, false, false, false), new[] { running })
                with
                {
                    AsusAuraSdk = aura,
                    MsiMysticLightSdk = mysticLight,
                };

        private static DirectUnit Unit(ushort vid, ushort pid, params VendorProgram[] programs)
            => new(OutputFamily.HidLightingUnit, "unit", Box, vid, pid) { Programs = programs };

        private static DeviceLinks Link(LinkRow row, PeripheralPresence presence, params DirectUnit[] units)
            => PeripheralLinker.Build(new[] { row }, new LinkInputs(HidppSnapshot.Empty, presence) { Units = units }).For(A);

        private static OutputPath Aura(string key) => new(OutputFamily.AuraSdkType, key);
        private static OutputPath Msi(string key) => new(OutputFamily.MysticLightType, key);

        [Fact]
        public void AnAsusKeyboard_IsLitThroughTheAuraKeyboardPath_WhileArmouryCrateRuns()
        {
            var row = new LinkRow(A, InputDeviceType.Keyboard, 0x0B05, 0x1866, Box, null);
            var unit = Unit(0x0B05, 0x1866, VendorPrograms.ArmouryCrate);
            var links = Link(row, Presence("LightingService.exe"), unit);
            Assert.Equal(new[] { Aura("keyboard") }, links.Lighting);
            Assert.True(links.DirectWhenClosed);
            Assert.Null(links.LeftToProgram);
            Assert.True(links.HasVendorProgram);

            // Closed: the device's own unit again.
            links = Link(row, Presence("explorer.exe"), unit);
            Assert.Equal(new[] { unit.Path }, links.Lighting);

            // No Aura SDK: the device is left to Armoury Crate, as before.
            links = Link(row, Presence("LightingService.exe", aura: false), unit);
            Assert.Empty(links.Lighting);
            Assert.Equal("Armoury Crate", links.LeftToProgram);
        }

        [Fact]
        public void AnAsusMouseNoFamilyLights_TakesTheAuraMousePath_OnlyWithTheSdk()
        {
            var row = new LinkRow(A, InputDeviceType.Mouse, 0x0B05, 0x1A70, Box, null);
            var links = Link(row, Presence("ArmouryCrate.Service.exe"));
            Assert.Equal(new[] { Aura("mouse") }, links.Lighting);
            Assert.False(links.DirectWhenClosed);
            Assert.Null(Link(row, Presence("ArmouryCrate.Service.exe", aura: false)));
            Assert.Null(Link(row, Presence("explorer.exe")));
            // Another vendor's mouse is no business of Armoury Crate's.
            Assert.Null(Link(row with { VendorId = 0x1234 }, Presence("ArmouryCrate.Service.exe")));
        }

        [Fact]
        public void AnMsiDevice_IsLitThroughMysticLight_WhileItRuns()
        {
            var keyboard = new LinkRow(A, InputDeviceType.Keyboard, 0x1462, 0x3FA4, Box, null);
            var unit = Unit(0x1462, 0x3FA4, VendorPrograms.MsiMysticLight);
            var links = Link(keyboard, Presence("Mystic_Light_Service.exe"), unit);
            Assert.Equal(new[] { Msi("other") }, links.Lighting);
            Assert.True(links.DirectWhenClosed);

            var mouse = keyboard with { CapType = InputDeviceType.Mouse };
            Assert.Equal(new[] { Msi("MSI_MOUSE") }, Link(mouse, Presence("Mystic_Light_Service.exe"), unit).Lighting);

            links = Link(keyboard, Presence("Mystic_Light_Service.exe", mysticLight: false), unit);
            Assert.Empty(links.Lighting);
            Assert.Equal("Mystic Light", links.LeftToProgram);

            Assert.Equal(new[] { unit.Path }, Link(keyboard, Presence("explorer.exe"), unit).Lighting);
            // An MSI device with no family of its own has no vendor rule.
            Assert.Null(Link(keyboard, Presence("Mystic_Light_Service.exe")));
        }

        [Fact]
        public void TheVendorRows_TakeEveryPathOfTheirSdk_WhileItIsInstalled()
        {
            var aura = new LinkRow(PeripheralOutputRow.IdentityFor(PeripheralRowKind.AsusAura), 0, 0, 0, Guid.Empty,
                PeripheralRowKind.AsusAura);
            var links = PeripheralLinker.Build(new[] { aura }, HidppSnapshot.Empty, Presence("explorer.exe"))
                .For(aura.Device);
            Assert.Equal(new[] { "keyboard", "mouse", "headset", "mainboard", "strip", "vga", "dram", "other" }.Select(Aura),
                links.Lighting);
            Assert.True(links.CatchAll);
            Assert.Null(PeripheralLinker.Build(new[] { aura }, HidppSnapshot.Empty, Presence("explorer.exe", aura: false))
                .For(aura.Device));

            var msi = new LinkRow(PeripheralOutputRow.IdentityFor(PeripheralRowKind.MsiMysticLight), 0, 0, 0, Guid.Empty,
                PeripheralRowKind.MsiMysticLight);
            links = PeripheralLinker.Build(new[] { msi }, HidppSnapshot.Empty, Presence("explorer.exe")).For(msi.Device);
            Assert.Equal(new[] { "MSI_MB", "MSI_VGA", "MSI_MOUSE", "other" }.Select(Msi), links.Lighting);
            Assert.Null(PeripheralLinker.Build(new[] { msi }, HidppSnapshot.Empty, Presence("explorer.exe", mysticLight: false))
                .For(msi.Device));
        }

        [Fact]
        public void TheVendorRowsOpen_WhileTheirSdkIsInstalled()
        {
            Assert.True(InputManager.PeripheralRowWanted(PeripheralRowKind.AsusAura, Presence("x", aura: true)));
            Assert.False(InputManager.PeripheralRowWanted(PeripheralRowKind.AsusAura, Presence("x", aura: false)));
            Assert.True(InputManager.PeripheralRowWanted(PeripheralRowKind.MsiMysticLight, Presence("x", mysticLight: true)));
            Assert.False(InputManager.PeripheralRowWanted(PeripheralRowKind.MsiMysticLight, Presence("x", mysticLight: false)));
            Assert.True(InputManager.PeripheralRowWanted(PeripheralRowKind.CorsairIcue, Presence("x") with { CorsairIcueSdk = true }));
            Assert.False(InputManager.PeripheralRowWanted(PeripheralRowKind.CorsairIcue, Presence("x")));
        }

        /// <summary>The Corsair iCUE row takes a path for every device type
        /// iCUE lights (#557), so a Corsair mousepad or headset stand, which
        /// no input row stands for and whose unit row is withheld while iCUE
        /// runs, lights through iCUE. With iCUE not installed no such path
        /// exists.</summary>
        [Fact]
        public void TheIcueRow_TakesEveryIcueType_WhileICueIsInstalled()
        {
            var icue = new LinkRow(PeripheralOutputRow.IdentityFor(PeripheralRowKind.CorsairIcue), 0, 0, 0, Guid.Empty,
                PeripheralRowKind.CorsairIcue);
            var links = PeripheralLinker.Build(new[] { icue }, HidppSnapshot.Empty, Presence("explorer.exe") with { CorsairIcueSdk = true })
                .For(icue.Device);
            Assert.Equal(new[] { "keyboard", "mouse", "mousemat", "headsetstand" }.Select(k => new OutputPath(OutputFamily.IcueDevice, k)),
                links.Lighting);
            Assert.True(links.CatchAll);
            Assert.True(links.OutputOnly);
            Assert.Null(PeripheralLinker.Build(new[] { icue }, HidppSnapshot.Empty, Presence("explorer.exe")).For(icue.Device));
        }

        /// <summary>The tab names the program a path runs through, by the
        /// program table's own names.</summary>
        [Fact]
        public void TheRouteLine_NamesTheProgram()
        {
            Assert.Equal("Armoury Crate", PeripheralRouteText.LightingApp(OutputFamily.AuraSdkType));
            Assert.Equal("Mystic Light", PeripheralRouteText.LightingApp(OutputFamily.MysticLightType));
            Assert.True(Aura("keyboard").Shared);
            Assert.True(Msi("other").Shared);
        }

        /// <summary>The backends are made with the host's direct backends,
        /// each for its own family.</summary>
        [Fact]
        public void TheHostMakesBothBackends()
        {
            var backends = DirectBackends.Create();
            try
            {
                Assert.Single(backends.OfType<AuraSdkBackend>());
                Assert.Single(backends.OfType<MysticLightBackend>());
                Assert.Equal(OutputFamily.MysticLightType, backends.OfType<MysticLightBackend>().Single().Family);
            }
            finally
            {
                foreach (var backend in backends) backend.Dispose();
            }
        }
    }
}
