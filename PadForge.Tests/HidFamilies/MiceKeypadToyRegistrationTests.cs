using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>Every OpenRGB registration of the fourteen mouse, mousepad,
    /// gamepad, keypad and toy families ported here answers with a model of
    /// its family, by the generated registration table (#557), so a
    /// registration a family misses fails.</summary>
    public class MiceKeypadToyRegistrationTests
    {
        /// <summary>Each family by its OpenRGB folder, with the count of
        /// detector registrations OpenRGB has for it.</summary>
        public static IEnumerable<object[]> Families() => new[]
        {
            new object[] { "TrustController", 2 },
            new object[] { "AOCMouseController", 1 },
            new object[] { "AOCMousematController", 1 },
            new object[] { "GigabyteAorusMouseController", 1 },
            new object[] { "LaviewTechnologyController", 1 },
            new object[] { "LexipMouseController", 1 },
            new object[] { "MSIClutchController", 1 },
            new object[] { "N5312AController", 1 },
            new object[] { "NZXTMouseController", 1 },
            new object[] { "PatriotViperMouseController", 1 },
            new object[] { "TecknetController", 1 },
            new object[] { "GameSirController", 2 },
            new object[] { "TurtleBeachKP7Controller", 1 },
            new object[] { "LegoDimensionsToypadBaseController", 1 },
        };

        internal static IHidLightingFamily FamilyOf(string folder) => folder switch
        {
            "TrustController" => TrustLighting.Instance,
            "AOCMouseController" => AocMouseLighting.Instance,
            "AOCMousematController" => AocMousematLighting.Instance,
            "GigabyteAorusMouseController" => GigabyteAorusMouseLighting.Instance,
            "LaviewTechnologyController" => LaviewTechnologyLighting.Instance,
            "LexipMouseController" => LexipMouseLighting.Instance,
            "MSIClutchController" => MsiClutchLighting.Instance,
            "N5312AController" => N5312ALighting.Instance,
            "NZXTMouseController" => NzxtMouseLighting.Instance,
            "PatriotViperMouseController" => PatriotViperMouseLighting.Instance,
            "TecknetController" => TecknetLighting.Instance,
            "GameSirController" => GameSirLighting.Instance,
            "TurtleBeachKP7Controller" => TurtleBeachKP7Lighting.Instance,
            "LegoDimensionsToypadBaseController" => LegoDimensionsToypadLighting.Instance,
            _ => null,
        };

        [Theory]
        [MemberData(nameof(Families))]
        public void EveryRegistration_AnswersWithAModelOfItsFamily(string folder, int count)
        {
            var family = FamilyOf(folder);
            var registrations = OpenRgbRegistrations.Of(folder).ToList();
            Assert.Equal(count, registrations.Count);
            foreach (var r in registrations)
            {
                var collection = CollectionFor(r, output: 512, feature: 512, input: 64);
                var model = family.Match(collection);
                Assert.True(model != null, $"{folder} {r.Name} {r.ProductId:X4}");
                Assert.Same(family, model.Family);
                Assert.Equal(r.Name, model.Name);
                Assert.True(model.Map.Lights.Count > 0);
                Assert.NotNull(model.Begin);
                Assert.Same(model, family.Match(collection));
                Assert.Same(model, HidLightingFamilies.Match(collection));
                Assert.Null(family.Match(collection with { VendorId = (ushort)(r.VendorId ^ 0x5555) }));
                // A program a model names is on the presence's list.
                Assert.All(model.Programs, p => Assert.Contains(p, VendorPrograms.All));
            }
        }

        [Fact]
        public void TheFourteenFamilies_AreInTheWorkersList_Once_TogetherInOrder()
        {
            var families = Families().Select(row => FamilyOf((string)row[0])).ToList();
            foreach (var family in families)
                Assert.Single(HidLightingFamilies.All, f => ReferenceEquals(f, family));
            int first = HidLightingFamilies.All.ToList().IndexOf(families[0]);
            Assert.Equal(families, HidLightingFamilies.All.Skip(first).Take(families.Count));
        }

        [Fact]
        public void ACollectionOfAnotherFamily_IsNoneOfTheirs()
        {
            // The AOC keyboard shares the AOC vendor ID: its collection is
            // the keyboard family's, not a mouse's or a mat's.
            var keyboard = CollectionFor(OpenRgbRegistrations.Of("AOCKeyboardController").First(), output: 64, feature: 361);
            Assert.Null(AocMouseLighting.Instance.Match(keyboard));
            Assert.Null(AocMousematLighting.Instance.Match(keyboard));
            foreach (object[] row in Families())
                Assert.Null(FamilyOf((string)row[0]).Match(keyboard));
        }

        [Fact]
        public void TheVendorPrograms_AreNamedByTheirCitedProcesses()
        {
            Assert.True(VendorPrograms.GloriousCore.Owns("Glorious Core"));
            Assert.True(VendorPrograms.AorusEngine.Owns("AORUS"));
            Assert.True(VendorPrograms.RgbFusion.Owns("RGBFusion"));
            Assert.True(VendorPrograms.RgbFusion.Owns("GvLedServices"));
            Assert.True(VendorPrograms.NzxtCam.Owns("NZXT CAM"));
            Assert.True(VendorPrograms.NzxtCam.Owns("cam_helper"));
            Assert.True(VendorPrograms.PatriotViperMouse.Owns("PATRIOT VIPER MOUSE"));
            Assert.True(VendorPrograms.TurtleBeachSwarmII.Owns("Turtle Beach Swarm II"));
            Assert.True(VendorPrograms.TurtleBeachSwarmII.Owns("Turtle Beach Device Service"));
            Assert.False(VendorPrograms.NzxtCam.Owns("explorer"));
            Assert.Equal(new[] { VendorPrograms.GloriousCore }, VendorPrograms.Glorious);
            Assert.Equal(new[] { VendorPrograms.AorusEngine, VendorPrograms.RgbFusion }, VendorPrograms.GigabyteMice);
            Assert.Equal(new[] { VendorPrograms.MsiMysticLight }, VendorPrograms.MsiClutch);
            Assert.Equal(new[] { VendorPrograms.NzxtCam }, VendorPrograms.Nzxt);
            Assert.Equal(new[] { VendorPrograms.PatriotViperMouse }, VendorPrograms.PatriotViper);
            Assert.Equal(new[] { VendorPrograms.TurtleBeachSwarmII }, VendorPrograms.TurtleBeach);
            foreach (var program in new[]
                     {
                         VendorPrograms.GloriousCore, VendorPrograms.AorusEngine, VendorPrograms.RgbFusion,
                         VendorPrograms.NzxtCam, VendorPrograms.PatriotViperMouse, VendorPrograms.TurtleBeachSwarmII,
                     })
                Assert.Equal(1, VendorPrograms.All.Count(p => ReferenceEquals(p, program)));
        }

        [Fact]
        public void EachModel_NamesItsPrograms_AndTheOthersNone()
        {
            HidLightingModel ModelOf(string folder)
                => FamilyOf(folder).Match(CollectionFor(OpenRgbRegistrations.Of(folder).First(), output: 512, feature: 512, input: 64));
            Assert.Same(VendorPrograms.Glorious, ModelOf("LaviewTechnologyController").Programs);
            Assert.Same(VendorPrograms.GigabyteMice, ModelOf("GigabyteAorusMouseController").Programs);
            Assert.Same(VendorPrograms.MsiClutch, ModelOf("MSIClutchController").Programs);
            Assert.Same(VendorPrograms.Nzxt, ModelOf("NZXTMouseController").Programs);
            Assert.Same(VendorPrograms.PatriotViper, ModelOf("PatriotViperMouseController").Programs);
            Assert.Same(VendorPrograms.TurtleBeach, ModelOf("TurtleBeachKP7Controller").Programs);
            foreach (var folder in new[]
                     {
                         "TrustController", "AOCMouseController", "AOCMousematController", "LexipMouseController",
                         "N5312AController", "TecknetController", "GameSirController", "LegoDimensionsToypadBaseController",
                     })
                Assert.Empty(ModelOf(folder).Programs);
        }
    }
}
