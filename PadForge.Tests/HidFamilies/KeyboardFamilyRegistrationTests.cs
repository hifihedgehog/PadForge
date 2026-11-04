using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>Every OpenRGB registration of the sixteen keyboard families
    /// ported here answers with a model of its family, by the generated
    /// registration table (#557), so a registration a family misses fails.</summary>
    public class KeyboardFamilyRegistrationTests
    {
        /// <summary>Each family by its OpenRGB folder.</summary>
        public static IEnumerable<object[]> Families() => new[]
        {
            new object[] { "MountainKeyboardController" },
            new object[] { "MintakaKeyboardController" },
            new object[] { "SkyloongController" },
            new object[] { "WitmodKeyboardController" },
            new object[] { "WinbondGamingKeyboardController" },
            new object[] { "ZETKeyboardController" },
            new object[] { "XPGSummonerKeyboardController" },
            new object[] { "OKSController" },
            new object[] { "LightSaltController" },
            new object[] { "DarkProject" },
            new object[] { "MNTKeyboardController" },
            new object[] { "FnaticStreakController" },
            new object[] { "ThermaltakePoseidonZRGBController" },
            new object[] { "SayoDeviceController" },
            new object[] { "HYTEKeyboardController" },
            new object[] { "AOCKeyboardController" },
        };

        private static IHidLightingFamily FamilyOf(string folder) => folder switch
        {
            "MountainKeyboardController" => MountainKeyboardLighting.Instance,
            "MintakaKeyboardController" => MintakaKeyboardLighting.Instance,
            "SkyloongController" => SkyloongKeyboardLighting.Instance,
            "WitmodKeyboardController" => WitmodKeyboardLighting.Instance,
            "WinbondGamingKeyboardController" => WinbondKeyboardLighting.Instance,
            "ZETKeyboardController" => ZetKeyboardLighting.Instance,
            "XPGSummonerKeyboardController" => XpgSummonerLighting.Instance,
            "OKSController" => OksKeyboardLighting.Instance,
            "LightSaltController" => LightSaltLighting.Instance,
            "DarkProject" => DarkProjectKeyboardLighting.Instance,
            "MNTKeyboardController" => MntKeyboardLighting.Instance,
            "FnaticStreakController" => FnaticStreakLighting.Instance,
            "ThermaltakePoseidonZRGBController" => PoseidonZRgbLighting.Instance,
            "SayoDeviceController" => SayoDeviceLighting.Instance,
            "HYTEKeyboardController" => HyteKeyboardLighting.Instance,
            "AOCKeyboardController" => AocKeyboardLighting.Instance,
            _ => null,
        };

        [Theory]
        [MemberData(nameof(Families))]
        public void EveryRegistration_AnswersWithAModelOfItsFamily(string folder)
        {
            var family = FamilyOf(folder);
            var registrations = OpenRgbRegistrations.Of(folder).ToList();
            Assert.NotEmpty(registrations);
            foreach (var r in registrations)
            {
                // Report lengths long enough for every family's frames.
                var collection = CollectionFor(r, output: 512, feature: 512, input: 64);
                var model = family.Match(collection);
                Assert.True(model != null, $"{folder} {r.Name} {r.ProductId:X4}");
                Assert.Same(family, model.Family);
                Assert.Equal(r.Name, model.Name);
                Assert.True(model.Map.Lights.Count > 0);
                Assert.NotNull(model.Begin);
                // A product's collections answer with one model.
                Assert.Same(model, family.Match(collection));
                // The shared worker reaches it through the family list.
                Assert.Same(model, HidLightingFamilies.Match(collection));
                // Another vendor's collection with the same fields is not it.
                Assert.Null(family.Match(collection with { VendorId = (ushort)(r.VendorId ^ 0x5555) }));
            }
        }

        [Fact]
        public void TheSixteenFamilies_AreInTheWorkersList_Once()
        {
            foreach (object[] row in Families())
            {
                var family = FamilyOf((string)row[0]);
                Assert.Single(HidLightingFamilies.All, f => ReferenceEquals(f, family));
            }
        }

        [Fact]
        public void TheGeneratedTables_AreReadAtThePinnedCommit()
        {
            Assert.Equal(KeyboardLayouts.OpenRgbCommit, KeyboardLedNames.OpenRgbCommit);
            Assert.Equal("c6fc8016d215cca17d660c47746a08d7ea6a9175", KeyboardLedNames.OpenRgbCommit);
        }

        [Fact]
        public void EveryKeyName_InTheTables_IsAUsageOrALabelOrAnOtherLight()
        {
            // The names OpenRGB's key names give a usage become keys, every
            // other name an Other light; no name in the tables is lost.
            var tables = new[]
            {
                KeyboardLedNames.MountainNames, KeyboardLedNames.ZetNames, KeyboardLedNames.XpgNames,
                KeyboardLedNames.OksNames, KeyboardLedNames.LightSaltKeyboardNames, KeyboardLedNames.LightSaltKeypadNames,
                KeyboardLedNames.DarkProjectNames, KeyboardLedNames.MntReformNames, KeyboardLedNames.MntPocketReformNames,
                KeyboardLedNames.FnaticFullNames, KeyboardLedNames.FnaticMiniNames, KeyboardLedNames.PoseidonNames,
            };
            foreach (var name in tables.SelectMany(t => t).Where(n => !string.IsNullOrEmpty(n)))
            {
                var light = KeyboardLedNames.Named(7, name);
                Assert.NotNull(light);
                Assert.Equal(7, light.Value.Address);
                if (light.Value.Kind == LightKind.Key)
                    Assert.True(OpenRgbKeys.TryGet(name, out ushort usage, out _) && usage == light.Value.Usage, name);
            }
            Assert.Null(KeyboardLedNames.Named(0, ""));
        }
    }
}
