using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A device of Logitech, Razer or SteelSeries that the shared worker
    /// lights (#557): the Logitech G600 (OpenRGB
    /// LogitechControllerDetect.cpp:1547) and the SteelSeries controllers of
    /// MSI laptops' keyboards (MSILaptopControllerDetect.cpp:330-333). The
    /// linker reads those vendors' rows for the shared worker's units too,
    /// under the vendor's program rule and the programs the unit's model
    /// names, and such a unit is no orphan while its own row reaches it.
    /// </summary>
    public class VendorRowSharedWorkerTests
    {
        private static readonly Guid A = new("00000000-0000-0000-0000-0000000000a1");
        private static readonly Guid Box = new("11111111-2222-3333-4444-555555555555");
        private static readonly Guid Pc = PeripheralLinker.LocalMachineContainer;

        private const ushort Logitech = PeripheralLinker.LogitechVid;
        private const ushort Razer = PeripheralLinker.RazerVid;
        private const ushort SteelSeries = PeripheralLinker.SteelSeriesVid;

        private static LinkRow Row(int capType, ushort vid, ushort pid, Guid container)
            => new(A, capType, vid, pid, container, null);

        private static LinkInputs Inputs(PeripheralPresence presence, params DirectUnit[] units)
            => new(HidppSnapshot.Empty, presence) { Units = units };

        private static PeripheralPresence Running(params string[] images)
            => PeripheralPresence.WithRunning(PeripheralPresence.None, images);

        /// <summary>A unit of the shared worker as its backend publishes it
        /// (HidLightingBackend.Publish), with the programs its model
        /// names.</summary>
        private static DirectUnit Shared(string key, Guid container, ushort vid, ushort pid, params VendorProgram[] programs)
            => new(OutputFamily.HidLightingUnit, key, container, vid, pid) { Programs = programs };

        private static DirectUnit G600 => Shared("g600", Box, Logitech, 0xC24A, VendorPrograms.Logitech.ToArray());

        /// <summary>The keyboard controller of an MSI Raider A18, 1038:1122
        /// (MSILaptopControllerDetect.cpp:330), whose model names
        /// SteelSeries GG.</summary>
        private static DirectUnit MsiKeyboard => Shared("klc", Pc, SteelSeries, 0x1122, VendorPrograms.SteelSeriesGG);

        private static OutputPath PathOf(DirectUnit unit) => new(OutputFamily.HidLightingUnit, unit.Key);

        /// <summary>Each of the three vendors' rows of the unit's own device
        /// lights it on its own, mouse and keyboard collection alike, and
        /// leaves no orphan.</summary>
        [Fact]
        public void AVendorRow_LightsTheSharedWorkerUnitOfItsOwnDevice()
        {
            foreach (ushort vendor in new[] { Logitech, Razer, SteelSeries })
            {
                var unit = Shared("u", Box, vendor, 0x0BEE);
                foreach (int capType in new[] { InputDeviceType.Mouse, InputDeviceType.Keyboard })
                {
                    var row = Row(capType, vendor, 0x0BEE, Box);
                    var links = PeripheralLinker.Build(new[] { row }, Inputs(PeripheralPresence.None, unit)).For(A);
                    Assert.Equal(new[] { PathOf(unit) }, links.Lighting);
                    Assert.True(links.HasVendorProgram);
                    Assert.Null(links.LeftToProgram);
                    Assert.Empty(PeripheralLinker.Orphans(new[] { row }, Inputs(PeripheralPresence.None, unit)));
                    Assert.True(PeripheralLinker.Reaches(row, unit, new Dictionary<Guid, int>()));
                }
                // Another product of the vendor, or the same one in another
                // container, does not reach it.
                foreach (var other in new[] { Row(InputDeviceType.Mouse, vendor, 0x0BEF, Box), Row(InputDeviceType.Mouse, vendor, 0x0BEE, Pc) })
                {
                    Assert.False(PeripheralLinker.Reaches(other, unit, new Dictionary<Guid, int>()));
                    Assert.Equal(new[] { unit }, PeripheralLinker.Orphans(new[] { other }, Inputs(PeripheralPresence.None, unit)));
                }
            }
        }

        /// <summary>The G600's mouse row lights it, beside the vendor's own
        /// families, and the MSI laptop's keyboard row lights its
        /// controller.</summary>
        [Fact]
        public void TheG600AndAnMsiLaptopKeyboard_AreLitThroughTheirRows()
        {
            var g600 = G600;
            var mouse = Row(InputDeviceType.Mouse, Logitech, 0xC24A, Box);
            Assert.Equal(new[] { PathOf(g600) }, PeripheralLinker.Build(new[] { mouse }, Inputs(PeripheralPresence.None, g600)).For(A).Lighting);

            var msi = MsiKeyboard;
            var keyboard = Row(InputDeviceType.Keyboard, SteelSeries, 0x1122, Pc);
            Assert.Equal(new[] { PathOf(msi) }, PeripheralLinker.Build(new[] { keyboard }, Inputs(PeripheralPresence.None, msi)).For(A).Lighting);
            Assert.Empty(PeripheralLinker.Orphans(new[] { keyboard }, Inputs(PeripheralPresence.None, msi)));
        }

        /// <summary>While the vendor's program runs the unit is that
        /// program's, as the vendor's own families are: the shared path where
        /// the vendor's SDK is there, else no path, and the tab names the
        /// model's program that runs.</summary>
        [Fact]
        public void WhileTheVendorsProgramRuns_TheUnitIsLeftToIt()
        {
            var g600 = G600;
            var mouse = Row(InputDeviceType.Mouse, Logitech, 0xC24A, Box);

            var ghub = PeripheralLinker.Build(new[] { mouse }, Inputs(Running("lghub_agent.exe"), g600)).For(A);
            Assert.Empty(ghub.Lighting);
            Assert.Equal("Logitech G HUB", ghub.LeftToProgram);

            // G HUB with its LED SDK: a Logitech row takes an LED SDK path
            // from its HID++ units alone, so a G600 row holds none, never
            // the unit's, and names G HUB as before.
            var withSdk = Running("lghub_agent.exe") with { LogitechLedSdk = true };
            var shared = PeripheralLinker.Build(new[] { mouse }, Inputs(withSdk, g600)).For(A);
            Assert.Empty(shared.Lighting);
            Assert.Equal("Logitech G HUB", shared.LeftToProgram);

            // A Logitech Gaming Software host.
            Assert.Equal("Logitech Gaming Software",
                PeripheralLinker.Build(new[] { mouse }, Inputs(Running("LCore.exe"), g600)).For(A).LeftToProgram);

            // The vendor's program holds the unit even when its model names
            // none: a unit with no programs under SteelSeries GG.
            var plain = Shared("plain", Box, SteelSeries, 0x1122);
            var row = Row(InputDeviceType.Keyboard, SteelSeries, 0x1122, Box);
            var gg = PeripheralLinker.Build(new[] { row }, Inputs(Running("SteelSeriesGG.exe"), plain)).For(A);
            Assert.True(gg == null || gg.Lighting.Length == 0);
            Assert.Empty(PeripheralLinker.Orphans(new[] { row }, Inputs(Running("SteelSeriesGG.exe"), plain)));
            // Another vendor's program does not.
            Assert.Equal(new[] { PathOf(plain) },
                PeripheralLinker.Build(new[] { row }, Inputs(Running("lghub_agent.exe"), plain)).For(A).Lighting);
        }

        /// <summary>A program the unit's model names holds it under a vendor
        /// row too, though the vendor's own program does not run.</summary>
        [Fact]
        public void AProgramTheModelNames_HoldsTheUnit_UnderAVendorRow()
        {
            var unit = Shared("klc", Box, SteelSeries, 0x1122, VendorPrograms.MsiMysticLight);
            var row = Row(InputDeviceType.Keyboard, SteelSeries, 0x1122, Box);
            Assert.Equal(new[] { PathOf(unit) }, PeripheralLinker.Build(new[] { row }, Inputs(PeripheralPresence.None, unit)).For(A).Lighting);
            var held = PeripheralLinker.Build(new[] { row }, Inputs(Running("Mystic_Light_Service.exe"), unit)).For(A);
            Assert.Empty(held.Lighting);
            Assert.Equal("Mystic Light", held.LeftToProgram);
        }

        /// <summary>A vendor row whose device has a unit of the vendor's own
        /// family and one of the shared worker lights both, its own family
        /// first.</summary>
        [Fact]
        public void AVendorRow_LightsItsOwnFamilysUnit_AndTheSharedOne()
        {
            var own = new DirectUnit(OutputFamily.SteelSeriesUnit, "rival", Box, SteelSeries, 0x1870);
            var shared = Shared("u", Box, SteelSeries, 0x1870);
            var row = Row(InputDeviceType.Mouse, SteelSeries, 0x1870, Box);
            Assert.Equal(new[] { own.Path, PathOf(shared) },
                PeripheralLinker.Build(new[] { row }, Inputs(PeripheralPresence.None, own, shared)).For(A).Lighting);
        }
    }
}
