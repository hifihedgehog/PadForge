using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>Every OpenRGB registration of the fifteen speaker, desk-light,
    /// light-strip and light-bar families ported here (#557) answers with a
    /// model of its family: a HID registration through the family list's
    /// match, a USB serial one through its family's look. The two the lane
    /// does not build are the ones named.</summary>
    public class DeskLightFamilyRegistrationTests
    {
        private static readonly Guid Box = new("11111111-2222-3333-4444-555555555555");

        /// <summary>Each HID family by its OpenRGB folder.</summary>
        public static IEnumerable<object[]> HidFamilies() => new[]
        {
            new object[] { "CreativeController" },
            new object[] { "EdifierHaloController" },
            new object[] { "SkydimoHIDController" },
            new object[] { "DreamCheekyController" },
            new object[] { "LuxaforController" },
            new object[] { "MadCatzCyborgController" },
            new object[] { "ThingMController" },
            new object[] { "StreamDeckController" },
            new object[] { "WushiController" },
            new object[] { "RobobloqLightStripController" },
            new object[] { "ClevoLightbarController" },
        };

        private static IHidLightingFamily FamilyOf(string folder) => folder switch
        {
            "CreativeController" => CreativeSoundBlasterLighting.Instance,
            "EdifierHaloController" => EdifierHaloLighting.Instance,
            "SkydimoHIDController" => SkydimoHidLighting.Instance,
            "DreamCheekyController" => DreamCheekyLighting.Instance,
            "LuxaforController" => LuxaforLighting.Instance,
            "MadCatzCyborgController" => MadCatzCyborgLighting.Instance,
            "ThingMController" => ThingMBlinkLighting.Instance,
            "StreamDeckController" => StreamDeckLighting.Instance,
            "WushiController" => WushiDockLighting.Instance,
            "RobobloqLightStripController" => RobobloqLightStripLighting.Instance,
            "ClevoLightbarController" => ClevoLightbarLighting.Instance,
            "SkydimoSerialController" => SkydimoSerialLighting.Instance,
            "BlinkyTapeController" => BlinkyTapeLighting.Instance,
            "HYTEMousematController" => HyteMousematLighting.Instance,
            _ => null,
        };

        [Theory]
        [MemberData(nameof(HidFamilies))]
        public void EveryHidRegistration_AnswersWithAModelOfItsFamily(string folder)
        {
            var family = FamilyOf(folder);
            var registrations = OpenRgbRegistrations.Of(folder).Where(r => r.Transport == OpenRgbTransport.Hid).ToList();
            Assert.NotEmpty(registrations);
            foreach (var r in registrations)
            {
                var collection = CollectionFor(r, output: 1024, feature: 65, input: 65);
                var model = family.Match(collection);
                Assert.True(model != null, $"{folder} {r.Name} {r.ProductId:X4}");
                Assert.Same(family, model.Family);
                Assert.Equal(r.Name, model.Name);
                Assert.NotNull(model.Begin);
                Assert.NotNull(model.Map);
                Assert.Same(model, family.Match(collection));
                Assert.Same(model, HidLightingFamilies.Match(collection));
                Assert.Null(family.Match(collection with { VendorId = (ushort)(r.VendorId ^ 0x5555) }));
                Assert.Null(family.Match(collection with { ProductId = (ushort)(r.ProductId ^ 0x5555) }));
            }
        }

        [Fact]
        public void TheRegistrationsFieldsBeyondTheIds_AreKept()
        {
            // Interface 4 for the G6 and the Pixelbar, 0 for the SK0902, and
            // the usage page and usage where the detector names them.
            var g6 = OpenRgbRegistrations.Of("CreativeController").Single(r => r.Transport == OpenRgbTransport.Hid);
            Assert.Null(CreativeSoundBlasterLighting.Instance.Match(CollectionFor(g6, output: 64) with { InterfaceNumber = 3 }));
            var halo = OpenRgbRegistrations.Of("EdifierHaloController").Single();
            Assert.Null(EdifierHaloLighting.Instance.Match(CollectionFor(halo, output: 64) with { UsagePage = 0xFF00 }));
            var stand = OpenRgbRegistrations.Of("SkydimoHIDController").Single();
            Assert.Null(SkydimoHidLighting.Instance.Match(CollectionFor(stand, output: 64) with { InterfaceNumber = 1 }));
            var bar = OpenRgbRegistrations.Of("ClevoLightbarController").Single();
            Assert.Null(ClevoLightbarLighting.Instance.Match(CollectionFor(bar, feature: 8) with { Usage = 0x01 }));
            // The Stream Deck's detector takes interface 0 only, whatever the
            // registration says (ElgatoStreamDeckControllerDetect.cpp:23).
            var deck = OpenRgbRegistrations.Of("StreamDeckController").Single();
            Assert.NotNull(StreamDeckLighting.Instance.Match(CollectionFor(deck, output: 1024)));
            Assert.Null(StreamDeckLighting.Instance.Match(CollectionFor(deck, output: 1024) with
            {
                InterfaceNumber = 1,
                Path = @"\\?\hid#vid_0fd9&pid_0080&mi_01#7&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
            }));
        }

        [Fact]
        public void ACollectionTooShortForTheFrames_IsNotTaken()
        {
            Assert.Null(CreativeSoundBlasterLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of("CreativeController").Single(r => r.Transport == OpenRgbTransport.Hid), output: 32)));
            Assert.Null(StreamDeckLighting.Instance.Match(CollectionFor(OpenRgbRegistrations.Of("StreamDeckController").Single(), output: 512)));
            Assert.Null(WushiDockLighting.Instance.Match(CollectionFor(OpenRgbRegistrations.Of("WushiController").Single(), feature: 64)));
            Assert.Null(ThingMBlinkLighting.Instance.Match(CollectionFor(OpenRgbRegistrations.Of("ThingMController").Single(), feature: 8)));
            Assert.Null(MadCatzCyborgLighting.Instance.Match(CollectionFor(OpenRgbRegistrations.Of("MadCatzCyborgController").Single(), feature: 8)));
            Assert.Null(ClevoLightbarLighting.Instance.Match(CollectionFor(OpenRgbRegistrations.Of("ClevoLightbarController").Single(), feature: 7)));
            Assert.Null(RobobloqLightStripLighting.Instance.Match(CollectionFor(OpenRgbRegistrations.Of("RobobloqLightStripController").Single(), output: 64)));
            Assert.Null(DreamCheekyLighting.Instance.Match(CollectionFor(OpenRgbRegistrations.Of("DreamCheekyController").Single(), output: 8)));
            Assert.Null(LuxaforLighting.Instance.Match(CollectionFor(OpenRgbRegistrations.Of("LuxaforController").Single(), output: 8)));
            Assert.Null(EdifierHaloLighting.Instance.Match(CollectionFor(OpenRgbRegistrations.Of("EdifierHaloController").Single(), output: 63)));
            Assert.Null(SkydimoHidLighting.Instance.Match(CollectionFor(OpenRgbRegistrations.Of("SkydimoHIDController").Single(), output: 63)));
        }

        public static IEnumerable<object[]> SerialFamilies() => new[]
        {
            new object[] { "SkydimoSerialController" },
            new object[] { "BlinkyTapeController" },
            new object[] { "HYTEMousematController" },
        };

        private static IOwnLookFamily SerialFamily(string folder, IReadOnlyList<ComPort> ports) => folder switch
        {
            "SkydimoSerialController" => new SkydimoSerialLighting(() => ports, _ => Box, _ => null, null),
            "BlinkyTapeController" => new BlinkyTapeLighting(() => ports, _ => Box, _ => null),
            "HYTEMousematController" => new HyteMousematLighting(() => ports, _ => Box, _ => null, null),
            _ => null,
        };

        [Theory]
        [MemberData(nameof(SerialFamilies))]
        public void EverySerialRegistration_IsFoundByItsFamilysLook(string folder)
        {
            var registrations = OpenRgbRegistrations.Of(folder).ToList();
            Assert.NotEmpty(registrations);
            Assert.All(registrations, r => Assert.Equal(OpenRgbTransport.UsbSerial, r.Transport));
            var ports = registrations.Select((r, i) => new ComPort($"COM{10 + i}", r.Name,
                $@"USB\VID_{r.VendorId:X4}&PID_{r.ProductId:X4}\6&{i}&0&1")).ToList();
            ports.Add(new ComPort("COM40", "Other", @"USB\VID_0403&PID_6001\A1"));
            var family = SerialFamily(folder, ports);
            var found = family.Look();
            Assert.Equal(registrations.Count, found.Count);
            for (int i = 0; i < registrations.Count; i++)
            {
                Assert.Equal($@"\\.\COM{10 + i}", found[i].Key);
                Assert.Equal((ushort)registrations[i].VendorId, found[i].VendorId);
                Assert.Equal((ushort)registrations[i].ProductId, found[i].ProductId);
                Assert.Equal(Box, found[i].Container);
                Assert.Same(family, found[i].Model.Family);
                Assert.Null(family.Match(CollectionFor(registrations[i] with { Transport = OpenRgbTransport.Hid })));
            }
            // The worker's own instance is in the list.
            Assert.Single(HidLightingFamilies.All, f => ReferenceEquals(f, FamilyOf(folder)));
        }

        [Fact]
        public void TheTwoDevicesNotBuilt_AreTheAe5AndTheSol()
        {
            // The AE-5 is a PCIe card reached through its driver, the Sol is
            // driven over libusb on a vendor interface: both detectors are
            // plain REGISTER_DETECTOR lines with no IDs.
            var other = OpenRgbRegistrations.Of("CreativeController").Concat(OpenRgbRegistrations.Of("ThrustmasterSolController"))
                .Where(r => r.Transport == OpenRgbTransport.Other).Select(r => r.Name).ToList();
            Assert.Equal(new[] { "Creative SoundBlaster AE-5", "Thrustmaster Sol" }, other);
        }

        [Fact]
        public void TheFamilies_AreInTheWorkersList_Once_AndTheirProgramsInTheProgramList()
        {
            foreach (string folder in HidFamilies().Select(r => (string)r[0]).Concat(SerialFamilies().Select(r => (string)r[0])))
                Assert.Single(HidLightingFamilies.All, f => ReferenceEquals(f, FamilyOf(folder)));
            var mine = new[]
            {
                VendorPrograms.Skydimo, VendorPrograms.HyteNexus, VendorPrograms.LuxaforApp, VendorPrograms.AmBx,
                VendorPrograms.Blink1Control, VendorPrograms.ElgatoStreamDeck,
            };
            foreach (var program in mine)
                Assert.Single(VendorPrograms.All, p => ReferenceEquals(p, program));
            // The six programs sit together after the ones before them.
            var all = VendorPrograms.All.ToList();
            int at = all.IndexOf(VendorPrograms.Skydimo);
            Assert.True(at > all.IndexOf(VendorPrograms.OpenRgb));
            Assert.Equal(mine, all.Skip(at).Take(mine.Length));
        }

        [Fact]
        public void ThePrograms_AreKnownByTheProcessNamesTheSourcesGive()
        {
            Assert.True(VendorPrograms.Skydimo.Owns("SkyDimo"));
            Assert.True(VendorPrograms.HyteNexus.Owns("HYTE Nexus"));
            Assert.True(VendorPrograms.HyteNexus.Owns("HYTE Nexus Service"));
            Assert.True(VendorPrograms.HyteNexus.Owns("HYTE.Nexus.Service"));
            Assert.True(VendorPrograms.LuxaforApp.Owns("LuxaforApp"));
            Assert.True(VendorPrograms.Blink1Control.Owns("Blink1Control2"));
            Assert.True(VendorPrograms.ElgatoStreamDeck.Owns("StreamDeck"));
            Assert.True(VendorPrograms.AmBx.Owns("amBX_Engine"));
            Assert.True(VendorPrograms.AmBx.Owns("amBXFxGen"));
            Assert.False(VendorPrograms.ElgatoStreamDeck.Owns("explorer"));
            Assert.False(VendorPrograms.Skydimo.Owns("Sky"));
        }

        [Fact]
        public void EachModel_NamesItsVendorsProgram_OrNoneWhereNoSourceNamesOne()
        {
            HidLightingModel Hid(string folder) => FamilyOf(folder).Match(CollectionFor(
                OpenRgbRegistrations.Of(folder).First(r => r.Transport == OpenRgbTransport.Hid), output: 1024, feature: 65));
            Assert.Equal(new[] { VendorPrograms.Skydimo }, Hid("SkydimoHIDController").Programs);
            Assert.Equal(new[] { VendorPrograms.LuxaforApp }, Hid("LuxaforController").Programs);
            Assert.Equal(new[] { VendorPrograms.AmBx }, Hid("MadCatzCyborgController").Programs);
            Assert.Equal(new[] { VendorPrograms.Blink1Control }, Hid("ThingMController").Programs);
            Assert.Equal(new[] { VendorPrograms.ElgatoStreamDeck }, Hid("StreamDeckController").Programs);
            foreach (string folder in new[]
            {
                "CreativeController", "EdifierHaloController", "DreamCheekyController", "WushiController",
                "RobobloqLightStripController", "ClevoLightbarController",
            })
                Assert.Empty(Hid(folder).Programs);
            Assert.Equal(new[] { VendorPrograms.Skydimo }, SkydimoSerialLighting.Instance.Model.Programs);
            Assert.Equal(new[] { VendorPrograms.HyteNexus }, HyteMousematLighting.Instance.Model.Programs);
            Assert.Empty(BlinkyTapeLighting.Instance.Model.Programs);
        }
    }
}
