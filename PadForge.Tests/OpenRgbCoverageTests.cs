using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>Every OpenRGB registration of the families #557 ports is lit by
    /// a family of PadForge's: a HID registration answers with a model through
    /// the shared worker's family list, and a registration reached another way
    /// has the family that reaches it in that list. A registration a family
    /// misses fails here by name, with its OpenRGB source line.</summary>
    public class OpenRgbCoverageTests
    {
        /// <summary>The families the generated table holds
        /// (tools/gen_openrgb_registrations.py): the keyboard families of the
        /// issue's coverage table, and the HID peripheral families added under
        /// its first paragraph. #494's families speak their own protocols and
        /// have lists of their own.</summary>
        [Fact]
        public void TheTable_HoldsEveryPortedFamily()
        {
            var families = OpenRgbRegistrations.All.Select(r => r.Family).Distinct().ToList();
            Assert.Equal(97, families.Count);
            Assert.DoesNotContain("RazerController", families);
            Assert.DoesNotContain("SteelSeriesController", families);
            Assert.DoesNotContain("LogitechController", families);
            Assert.DoesNotContain("HIDLampArrayController", families);
        }

        /// <summary>A registration that answers with no model, and why. QMK's
        /// two take their IDs from the user's OpenRGB settings, so they match
        /// nothing by design (<see cref="OpenRgbRegistration.FromSettings"/>).</summary>
        private static readonly Dictionary<string, string> NoModelOnPurpose = new()
        {
            // The rows, layout or SKU come from the machine these families run
            // on, so a test machine that is not one answers no model. Each
            // family's own tests hand it the machine's strings.
            ["ASUS ROG Strix SCAR 15"] = "the board name picks the rows (AsusAuraCoreLightingTests)",
            ["Clevo Keyboard (ITE 8291)"] = "the SKU picks the layout (ClevoLightingTests)",
            ["MSI Laptop Keyboard"] = "the laptop's strings pick the layout (MsiLaptopLightingTests)",
            ["MSI Laptop Lightbar"] = "the laptop's strings pick the layout (MsiLaptopLightingTests)",
            ["MSI GE68HX Keyboard"] = "the laptop's strings pick the layout (MsiLaptopLightingTests)",
            ["MSI GE68HX Lightbar"] = "the laptop's strings pick the layout (MsiLaptopLightingTests)",
            // These mice answer on two collections, so an own-look family
            // finds them in the walk (SinowealthMouseLighting).
            ["Glorious Model O / O-"] = "two collections (SinowealthMouseLightingTests)",
            ["Glorious Model D / D-"] = "two collections (SinowealthMouseLightingTests)",
            ["Everest GT-100 RGB"] = "two collections (SinowealthMouseLightingTests)",
            ["Genesis Xenon 200"] = "two collections (SinowealthMouseLightingTests)",
        };

        /// <summary>The report lengths the families ask of a collection: some
        /// ask for one length exactly, so a registration is offered each.</summary>
        private static readonly ushort[] Lengths =
        {
            0, 7, 8, 9, 16, 17, 20, 21, 32, 33, 37, 49, 59, 61, 64, 65, 72, 78, 90, 91, 129, 145, 193, 256, 264, 321, 513, 520, 525, 1024,
        };

        /// <summary>The usage pages a family asks of a registration that names
        /// none: Wooting's two protocols go by the page
        /// (WootingKeyboardControllerDetect.cpp:121-171).</summary>
        private static readonly ushort[] Pages = { 0xFF00, 0x1337, 0xFF55 };

        /// <summary>The model a registration's collection answers with at any
        /// lengths, or null.</summary>
        private static HidLightingModel ModelOf(OpenRgbRegistration r)
        {
            foreach (ushort page in r.UsagePage == OpenRgbRegistration.Any ? Pages : new[] { (ushort)r.UsagePage })
                foreach (ushort output in Lengths)
                    foreach (ushort feature in Lengths)
                    {
                        var collection = CollectionFor(r, output: output, feature: feature, input: 64) with { UsagePage = page };
                        var model = HidLightingFamilies.Match(collection);
                        if (model != null) return model;
                    }
            return null;
        }

        /// <summary>Every HID registration with its own IDs answers with a
        /// model of the worker's families.</summary>
        [Fact]
        public void EveryHidRegistration_AnswersWithAModel()
        {
            var missed = new List<string>();
            foreach (var r in OpenRgbRegistrations.All)
            {
                if (r.Transport != OpenRgbTransport.Hid || r.FromSettings) continue;
                if (ModelOf(r) == null && !NoModelOnPurpose.ContainsKey(r.Name))
                    missed.Add($"{r.Family} {r.Name} {r.VendorId:X4}:{r.ProductId:X4} ({r.Source})");
            }
            Assert.True(missed.Count == 0, missed.Count + " registrations no family lights:\n" + string.Join("\n", missed));
        }

        /// <summary>A keyboard's table puts each key in its place: Q left of W on one row, and the rows in
        /// order. A table out of step lights other keys' LEDs with nothing in the frames to show it.</summary>
        [Fact]
        public void EveryKeyboardTable_KeepsItsKeysInKeyboardOrder()
        {
            var faults = new List<string>();
            var seen = new HashSet<string>();
            foreach (var r in OpenRgbRegistrations.All)
            {
                if (r.Transport != OpenRgbTransport.Hid || r.FromSettings) continue;
                var model = ModelOf(r);
                if (model?.Map == null) continue;
                var found = KeyTableOrderTests.Faults(model.Map.Lights).ToList();
                if (found.Count > 0 && seen.Add(r.Family + " " + r.Name))
                    faults.Add($"{r.Family} {r.Name} {r.VendorId:X4}:{r.ProductId:X4}: {found.Count}: {string.Join("; ", found.Take(4))}");
            }
            Assert.True(faults.Count == 0, faults.Count + " tables:\n" + string.Join("\n", faults));
        }

        /// <summary>What the tables get wrong in OpenRGB is put right here.</summary>
        [Fact]
        public void AKeyOpenRgbsTableNamesWrong_IsPutRight()
        {
            ushort At(IEnumerable<LightInfo> lights, int address) => lights.Single(l => l.Address == address).Usage;
            // The Everest 60's row runs C, V, B, N.
            var everest = MountainKeyboardLighting.Everest60Lights();
            Assert.Equal(0x19, everest.Single(l => l.Usage == 0x19).Usage);
            Assert.True(everest.Single(l => l.Usage == 0x19).X < everest.Single(l => l.Usage == 0x05).X);
            // The Strix Scope TKL's grid does not follow its list, and its names and LEDs do.
            var scope = AsusAuraUsbLighting.KeyboardMap(0x190C, "AsusROGStrixScopeTKLLayouts", AsusAuraTables.RegionUk);
            Assert.False(scope.Positioned);
            Assert.Equal(0x1E, At(scope.Lights, 0x11));
            Assert.Equal(0x14, At(scope.Lights, 0x12));
            // The Vulcan II TKL Pro has both of its keys, Y and Z, once.
            var model = ModelOf(OpenRgbRegistrations.All.First(r => r.Name == "Turtle Beach Vulcan II TKL Pro"));
            Assert.Single(model.Map.Lights, l => l.Usage == 0x1C);
            Assert.Single(model.Map.Lights, l => l.Usage == 0x1D);
        }

        /// <summary>The Thrustmaster Sol's lights are on a vendor interface that
        /// is no HID collection, which OpenRGB reaches through libusb and UsbDk
        /// (ThrustmasterSolControllerDetect.cpp:43-60), so it would take a
        /// driver PadForge does not install.</summary>
        private const string ThrustmasterSol = "ThrustmasterSolController";

        /// <summary>The registrations with no HID collection, which a family
        /// reaches through WMI, ACPI or a serial port.</summary>
        [Fact]
        public void EveryOtherRegistration_HasAFamilyThatLooksForIt()
        {
            var others = OpenRgbRegistrations.All.Where(r => r.Transport != OpenRgbTransport.Hid).Select(r => r.Family).Distinct().OrderBy(f => f).ToList();
            var families = new Dictionary<string, IHidLightingFamily>
            {
                ["AsusTUFLaptopController"] = AsusTufLaptopLighting.Instance,
                ["BlinkyTapeController"] = BlinkyTapeLighting.Instance,
                ["CreativeController"] = CreativeSoundBlasterLighting.Instance,
                ["DygmaRaiseController"] = DygmaRaiseLighting.Instance,
                ["HPOmenLaptopController"] = HpOmenLaptopLighting.Instance,
                ["HYTEMousematController"] = HyteMousematLighting.Instance,
                ["QMKController"] = QmkLighting.Instance,
                ["SkydimoSerialController"] = SkydimoSerialLighting.Instance,
            };
            Assert.Equal(families.Keys.Append(ThrustmasterSol).OrderBy(f => f, StringComparer.Ordinal), others.OrderBy(f => f, StringComparer.Ordinal));
            foreach (var family in families.Values)
                Assert.Contains(family, HidLightingFamilies.All);
        }

        /// <summary>The keyboards with a key no source names, sorted. OpenRGB
        /// gives the Cherry and EVision boards a grid of numbered lights and no
        /// key names. The others have a few keys it, uchroma and Polychromatic
        /// leave unnamed. Every light is lit, and Map Key Lights asks about each
        /// one with no name.</summary>
        private static readonly string[] NoKeySource =
        {
            "CherryKeyboardController Cherry Keyboard CCF MX 1.0 TKL BL",
            "CherryKeyboardController Cherry Keyboard CCF MX 1.0 TKL NBL",
            "CherryKeyboardController Cherry Keyboard CCF MX 8.0 TKL BL",
            "CherryKeyboardController Cherry Keyboard G80-3000 TKL NBL",
            "CherryKeyboardController Cherry Keyboard G80-3000 TKL NBL KOREAN",
            "CherryKeyboardController Cherry Keyboard G80-3000 TKL RGB",
            "CherryKeyboardController Cherry Keyboard G80-3000N FL RGB (EU)",
            "CherryKeyboardController Cherry Keyboard G80-3000N FL RGB (US)",
            "CherryKeyboardController Cherry Keyboard G80-3000N TKL RGB (EU)",
            "CherryKeyboardController Cherry Keyboard G80-3000N TKL RGB (US)",
            "CherryKeyboardController Cherry Keyboard MV BOARD 3.0 FL RGB",
            "CherryKeyboardController Cherry Keyboard MX 1.0 FL BL",
            "CherryKeyboardController Cherry Keyboard MX 1.0 FL NBL",
            "CherryKeyboardController Cherry Keyboard MX 1.0 FL RGB",
            "CherryKeyboardController Cherry Keyboard MX BOARD 1.0 TKL RGB",
            "CherryKeyboardController Cherry Keyboard MX BOARD 10.0 FL RGB",
            "CherryKeyboardController Cherry Keyboard MX BOARD 10.0N FL RGB (EU)",
            "CherryKeyboardController Cherry Keyboard MX BOARD 10.0N FL RGB (US)",
            "CherryKeyboardController Cherry Keyboard MX BOARD 2.0S FL NBL",
            "CherryKeyboardController Cherry Keyboard MX BOARD 2.0S FL RGB (EU)",
            "CherryKeyboardController Cherry Keyboard MX BOARD 2.0S FL RGB (US)",
            "CherryKeyboardController Cherry Keyboard MX BOARD 3.0S FL NBL",
            "CherryKeyboardController Cherry Keyboard MX BOARD 3.0S FL RGB",
            "CherryKeyboardController Cherry Keyboard MX BOARD 3.0S FL RGB KOREAN",
            "CherryKeyboardController Cherry Keyboard MX BOARD 8.0 TKL RGB",
            "EVGAUSBController EVGA Z20 Keyboard",
            "EVisionKeyboardController EVision Keyboard 0C45:5004",
            "EVisionKeyboardController EVision Keyboard 0C45:5104",
            "EVisionKeyboardController EVision Keyboard 0C45:5204",
            "EVisionKeyboardController EVision Keyboard 0C45:652F",
            "EVisionKeyboardController EVision Keyboard 0C45:7672",
            "EVisionKeyboardController EVision Keyboard 0C45:7698",
            "EVisionKeyboardController EVision Keyboard 0C45:8520",
            "EVisionKeyboardController EVision Keyboard 320F:5000",
            "EVisionKeyboardController EVision Keyboard 320F:502A",
            "EVisionKeyboardController EVision Keyboard 320F:505B",
            "EVisionKeyboardController EVision Keyboard 320F:5064",
            "EVisionKeyboardController EVision Keyboard 320F:5078",
            "EVisionKeyboardController EVision Keyboard 320F:5084",
            "LenovoControllers Lenovo Legion 7 Gen 10",
            "LenovoControllers Lenovo Legion 7 Gen 5",
            "LenovoControllers Lenovo Legion 7 Gen 6",
            "LenovoControllers Lenovo Legion 7 Gen 7",
            "LenovoControllers Lenovo Legion 7 Gen 8",
            "LenovoControllers Lenovo Legion 7 Gen 9",
            "LenovoControllers Lenovo Legion 7S Gen 5",
            "LenovoControllers Lenovo Legion 7S Gen 6",
            "LenovoControllers Lenovo Legion 7S Gen 7",
            "LenovoControllers Lenovo Legion 7S Gen 8",
            "Razer 0207",
            "Razer 020F",
            "Razer 0210",
            "Razer 0211",
            "Razer 0214",
            "Razer 0217",
            "Razer 021F",
            "Razer 0220",
            "Razer 0224",
            "Razer 022C",
            "Razer 022D",
            "Razer 022F",
            "Razer 0232",
            "Razer 0233",
            "Razer 0234",
            "Razer 0237",
            "Razer 0239",
            "Razer 023A",
            "Razer 023B",
            "Razer 0240",
            "Razer 0244",
            "Razer 0245",
            "Razer 024B",
            "Razer 024C",
            "Razer 024D",
            "Razer 0253",
            "Razer 0256",
            "Razer 0268",
            "Razer 0269",
            "Razer 026D",
            "Razer 026E",
            "Razer 028B",
            "Razer 029E",
            "Razer 029F",
            "Razer 02A0",
            "Razer 02B0",
            "Razer 02B6",
            "Razer 02B8",
            "Razer 02B9",
            "Razer 02BA",
            "Razer 02C5",
            "Razer 02C6",
            "Razer 02C7",
            "Razer 02CF",
            "Razer 02E4",
            "WootingKeyboardController Wooting 80HE",
            "WootingKeyboardController Wooting UwU RGB",
        };

        /// <summary>A keyboard joining this list is a keyboard to look for a
        /// source for, so it has to be added here by hand.</summary>
        [Fact]
        public void TheKeyboardsWithNoKeySource_AreTheseOnes()
        {
            var unnamed = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var row in RazerLightTable.Rows)
            {
                var map = RazerLightTable.Map(row.ProductId);
                if (map.UnnamedKeyCount > 0) unnamed.Add($"Razer {row.ProductId:X4}");
            }
            foreach (var r in OpenRgbRegistrations.All)
            {
                if (r.Transport != OpenRgbTransport.Hid || r.FromSettings) continue;
                var model = ModelOf(r);
                if (model != null && model.Map.UnnamedKeyCount > 0) unnamed.Add($"{r.Family} {r.Name}");
            }
            Assert.Equal(NoKeySource, unnamed.ToArray());
        }
    }
}
